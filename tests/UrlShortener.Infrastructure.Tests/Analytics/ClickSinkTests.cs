using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using NSubstitute;
using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.InMemory;

namespace UrlShortener.Infrastructure.Tests.Analytics;

public sealed class ClickEventSerializerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, 123, TimeSpan.Zero);

    [Fact]
    public void Round_trips_clicks_at_millisecond_precision()
    {
        ClickEvent[] clicks = [new("abc1234", T0), new("promo-24", T0.AddSeconds(1))];

        ClickEventSerializer.Deserialize(ClickEventSerializer.Serialize(clicks)).ShouldBe(clicks);
    }

    [Fact]
    public void Uses_the_compact_documented_wire_format()
    {
        var json = ClickEventSerializer.Serialize([new ClickEvent("abc1234", T0)]).ToString();

        json.ShouldBe($$"""{"v":1,"clicks":[{"c":"abc1234","t":{{T0.ToUnixTimeMilliseconds()}}}]}""");
    }

    [Fact]
    public void Rejects_unknown_versions() =>
        Should.Throw<NotSupportedException>(() => ClickEventSerializer.Deserialize(BinaryData.FromString("""{"v":2,"clicks":[]}""")));

    [Fact]
    public void Rejects_malformed_payloads() =>
        Should.Throw<JsonException>(() => ClickEventSerializer.Deserialize(BinaryData.FromString("not json")));
}

public sealed class DirectStatsSinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Aggregates_before_writing_to_the_store()
    {
        var store = new InMemoryStatsStore();
        var sink = new DirectStatsSink(store);

        await sink.PublishAsync([new("abc1234", T0), new("abc1234", T0.AddMinutes(1)), new("xyz9876", T0)], Ct);

        (await store.GetAsync("abc1234", Ct)).ShouldNotBeNull().TotalClicks.ShouldBe(2);
        (await store.GetAsync("xyz9876", Ct)).ShouldNotBeNull().TotalClicks.ShouldBe(1);
    }

    [Fact]
    public async Task Skips_the_store_for_an_empty_batch()
    {
        var store = Substitute.For<IStatsStore>();

        await new DirectStatsSink(store).PublishAsync([], Ct);

        await store.DidNotReceive().ApplyAsync(Arg.Any<IReadOnlyCollection<ClickAggregate>>(), Arg.Any<CancellationToken>());
    }
}

public sealed class EventHubsClickSinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly EventHubProducerClient _producer = Substitute.For<EventHubProducerClient>();
    private readonly List<List<EventData>> _sentBatches = [];
    private readonly Dictionary<EventDataBatch, List<EventData>> _backingStores = [];
    private int _maxEventsPerBatch = int.MaxValue;

    public EventHubsClickSinkTests()
    {
        // EventHubsModelFactory is the SDK's own seam for testing: a batch whose TryAdd we control.
#pragma warning disable CA2012 // NSubstitute configures the call; the ValueTask is never consumed here.
        _producer.CreateBatchAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            var store = new List<EventData>();
            var batch = EventHubsModelFactory.EventDataBatch(1_048_576, store, tryAddCallback: _ => store.Count < _maxEventsPerBatch);
            _backingStores[batch] = store;
            return new ValueTask<EventDataBatch>(batch);
        });
#pragma warning restore CA2012
        _producer.SendAsync(Arg.Any<EventDataBatch>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _sentBatches.Add([.. _backingStores[call.Arg<EventDataBatch>()]]); // copy before the sink disposes the batch
            return Task.CompletedTask;
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<ClickEvent> Clicks(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ClickEvent($"code{i % 7}", T0.AddMilliseconds(i)))];

    [Fact]
    public async Task Sends_nothing_for_an_empty_batch()
    {
        await new EventHubsClickSink(_producer).PublishAsync([], Ct);

        await _producer.DidNotReceive().CreateBatchAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Packs_clicks_into_events_of_at_most_500()
    {
        var clicks = Clicks(1_200);

        await new EventHubsClickSink(_producer).PublishAsync(clicks, Ct);

        var batch = _sentBatches.ShouldHaveSingleItem();
        batch.Select(e => ClickEventSerializer.Deserialize(e.EventBody).Count).ShouldBe([500, 500, 200]);
        batch.ShouldAllBe(e => e.ContentType == ClickEventSerializer.ContentType);
        batch.SelectMany(e => ClickEventSerializer.Deserialize(e.EventBody)).ShouldBe(clicks);
    }

    [Fact]
    public async Task Starts_a_new_batch_when_the_current_one_is_full()
    {
        _maxEventsPerBatch = 2;
        var clicks = Clicks(2_100); // 5 events: 2 + 2 + 1

        await new EventHubsClickSink(_producer).PublishAsync(clicks, Ct);

        _sentBatches.Select(b => b.Count).ShouldBe([2, 2, 1]);
        _sentBatches.SelectMany(b => b).SelectMany(e => ClickEventSerializer.Deserialize(e.EventBody)).ShouldBe(clicks);
    }

    [Fact]
    public async Task Fails_loudly_if_a_single_event_can_never_fit()
    {
        _maxEventsPerBatch = 0;

        await Should.ThrowAsync<InvalidOperationException>(() => new EventHubsClickSink(_producer).PublishAsync(Clicks(1), Ct));
    }
}
