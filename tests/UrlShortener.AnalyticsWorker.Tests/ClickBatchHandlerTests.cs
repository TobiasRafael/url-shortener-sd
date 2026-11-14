using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using UrlShortener.Core.Analytics;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.InMemory;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace UrlShortener.AnalyticsWorker.Tests;

/// <summary>
/// Drives the handler the way <see cref="EventProcessorClient"/> does, with events and
/// checkpoints built from the SDK's own test seams. Real Event Hubs runs in the AppHost tests.
/// </summary>
public sealed class ClickBatchHandlerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly FlakyStatsStore _store = new();
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly WorkerMetrics _metrics;
    private readonly ClickConsumerOptions _options = new()
    {
        FlushInterval = FlushInterval,
        MaxBatchClicks = 1_000,
        InitialRetryDelay = TimeSpan.Zero,
        MaxRetryDelay = TimeSpan.Zero,
    };

    /// <summary>Sequence numbers checkpointed, per partition, in order.</summary>
    private readonly ConcurrentDictionary<string, List<long>> _checkpoints = new();

    private long _sequenceNumber;

    public ClickBatchHandlerTests() => _metrics = new WorkerMetrics(_services.GetRequiredService<IMeterFactory>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _metrics.Dispose();
        _services.Dispose();
    }

    private ClickBatchHandler CreateHandler() =>
        new(_store, MsOptions.Create(_options), _metrics, _time, NullLogger<ClickBatchHandler>.Instance);

    private ProcessEventArgs Event(string partitionId, BinaryData body)
    {
        var sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        var data = EventHubsModelFactory.EventData(
            body,
            sequenceNumber: sequenceNumber,
            offsetString: sequenceNumber.ToString(CultureInfo.InvariantCulture),
            enqueuedTime: _time.GetUtcNow());

        return new ProcessEventArgs(Partition(partitionId), data, _ =>
        {
            _checkpoints.GetOrAdd(partitionId, _ => []).Add(sequenceNumber);
            return Task.CompletedTask;
        }, Ct);
    }

    private ProcessEventArgs Clicks(string partitionId, params (string Code, int Count)[] clicks) =>
        Event(partitionId, ClickEventSerializer.Serialize(
            clicks.SelectMany(c => Enumerable.Repeat(new ClickEvent(c.Code, _time.GetUtcNow()), c.Count))));

    /// <summary>What the processor sends when nothing arrived within MaximumWaitTime.</summary>
    private static ProcessEventArgs NoEvent(string partitionId, CancellationToken? stopping = null) =>
        new(Partition(partitionId), null, _ => throw new InvalidOperationException("Cannot checkpoint without an event."), stopping ?? Ct);

    private static PartitionContext Partition(string partitionId) =>
        EventHubsModelFactory.PartitionContext("test.servicebus.windows.net", "clicks", "analytics", partitionId);

    private async Task<long> TotalClicksAsync(string code) => (await _store.GetAsync(code, Ct))?.TotalClicks ?? 0;

    private List<long> CheckpointsOf(string partitionId) => _checkpoints.GetValueOrDefault(partitionId) ?? [];

    [Fact]
    public async Task Clicks_are_buffered_until_the_flush_interval_then_written_once_per_link_and_checkpointed()
    {
        var handler = CreateHandler();

        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 3)));
        _time.Advance(TimeSpan.FromSeconds(1));
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 2), ("xyz9876", 1)));

        _store.Writes.ShouldBeEmpty();
        CheckpointsOf("0").ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(1));
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 1)));

        // 7 clicks over 3 events become 2 writes: one per (link, day).
        _store.Writes.Select(w => (w.Code, w.Count)).ShouldBe([("abc1234", 6), ("xyz9876", 1)], ignoreOrder: true);
        (await TotalClicksAsync("abc1234")).ShouldBe(6);
        // One checkpoint, at the newest event, covers all three.
        CheckpointsOf("0").ShouldBe([3]);
    }

    [Fact]
    public async Task A_full_batch_is_written_without_waiting_for_the_interval()
    {
        _options.MaxBatchClicks = 500;
        var handler = CreateHandler();

        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 300)));
        _store.Writes.ShouldBeEmpty();

        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 200)));

        (await TotalClicksAsync("abc1234")).ShouldBe(500);
        CheckpointsOf("0").ShouldBe([2]);
    }

    [Fact]
    public async Task A_quiet_partition_still_flushes_when_the_processor_calls_without_an_event()
    {
        var handler = CreateHandler();
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 4)));

        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));

        (await TotalClicksAsync("abc1234")).ShouldBe(4);
        CheckpointsOf("0").ShouldBe([1]);
    }

    [Fact]
    public async Task Calls_without_events_and_nothing_buffered_do_nothing()
    {
        var handler = CreateHandler();

        for (var i = 0; i < 3; i++)
        {
            _time.Advance(FlushInterval);
            await handler.ProcessEventAsync(NoEvent("0"));
        }

        _store.Writes.ShouldBeEmpty();
        _checkpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_batch_is_written_and_checkpointed_only_once()
    {
        var handler = CreateHandler();
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 1)));
        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));

        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 1)));
        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));

        (await TotalClicksAsync("abc1234")).ShouldBe(2);
        CheckpointsOf("0").ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Partitions_batch_and_checkpoint_independently()
    {
        var handler = CreateHandler();
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 1)));
        _time.Advance(TimeSpan.FromSeconds(1));
        await handler.ProcessEventAsync(Clicks("1", ("xyz9876", 1)));

        _time.Advance(TimeSpan.FromSeconds(1));
        await handler.ProcessEventAsync(NoEvent("0"));
        await handler.ProcessEventAsync(NoEvent("1"));

        // Partition 0's batch is 2s old and flushes; partition 1's is 1s old and waits.
        CheckpointsOf("0").ShouldBe([1]);
        CheckpointsOf("1").ShouldBeEmpty();
        (await TotalClicksAsync("xyz9876")).ShouldBe(0);
    }

    [Fact]
    public async Task An_unreadable_event_is_skipped_and_the_checkpoint_moves_past_it()
    {
        var handler = CreateHandler();
        await handler.ProcessEventAsync(Event("0", BinaryData.FromString("not json")));
        await handler.ProcessEventAsync(Event("0", BinaryData.FromString("""{"v":99,"clicks":[]}""")));
        await handler.ProcessEventAsync(Event("0", BinaryData.FromString("""{"v":1}""")));

        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));

        _store.Writes.ShouldBeEmpty();
        // Not checkpointing would mean reading the same bad events again after every restart.
        CheckpointsOf("0").ShouldBe([3]);
    }

    [Fact]
    public async Task Clicks_for_codes_that_could_never_exist_are_dropped_instead_of_blocking_the_partition()
    {
        var handler = CreateHandler();
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 2), ("bad/code#", 5), ("x", 1)));

        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));

        _store.Writes.Select(w => w.Code).ShouldBe(["abc1234"]);
        CheckpointsOf("0").ShouldBe([1]);
    }

    [Fact]
    public async Task A_failing_store_is_retried_for_only_the_links_that_failed_and_checkpoints_after_success()
    {
        _store.FailNext("xyz9876", times: 2);
        var handler = CreateHandler();
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 3), ("xyz9876", 2)));

        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));

        // abc1234 was written once; retrying it with the others would have counted it twice.
        (await TotalClicksAsync("abc1234")).ShouldBe(3);
        (await TotalClicksAsync("xyz9876")).ShouldBe(2);
        _store.Attempts.Count(code => code == "abc1234").ShouldBe(1);
        _store.Attempts.Count(code => code == "xyz9876").ShouldBe(3);
        CheckpointsOf("0").ShouldBe([1]);
    }

    [Fact]
    public async Task Stopping_while_the_store_is_down_leaves_the_batch_uncheckpointed_for_replay()
    {
        _store.FailNext("abc1234", times: int.MaxValue);
        var handler = CreateHandler();
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 1)));
        _time.Advance(FlushInterval);

        using var stopping = new CancellationTokenSource();
        var flush = handler.ProcessEventAsync(NoEvent("0", stopping.Token));
        await _store.WaitForAttemptsAsync(3, Ct);
        await stopping.CancelAsync();

        await flush; // returns quietly instead of failing the processor
        CheckpointsOf("0").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_closed_partition_drops_its_buffer_so_the_next_owner_replays_it_exactly_once()
    {
        var handler = CreateHandler();
        await handler.InitializePartitionAsync(new PartitionInitializingEventArgs("0", EventPosition.Earliest, Ct));
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 5)));

        await handler.ClosePartitionAsync(new PartitionClosingEventArgs("0", ProcessingStoppedReason.OwnershipLost, Ct));

        // If the partition comes back here later, the replayed events are the only copy counted.
        await handler.InitializePartitionAsync(new PartitionInitializingEventArgs("0", EventPosition.Earliest, Ct));
        await handler.ProcessEventAsync(Clicks("0", ("abc1234", 5)));
        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));

        (await TotalClicksAsync("abc1234")).ShouldBe(5);
        CheckpointsOf("0").ShouldBe([2]);
    }

    [Fact]
    public async Task A_failed_checkpoint_does_not_fail_the_handler_or_rewrite_the_stats()
    {
        var handler = CreateHandler();
        var data = EventHubsModelFactory.EventData(
            ClickEventSerializer.Serialize([new ClickEvent("abc1234", T0)]), sequenceNumber: 1, offsetString: "1", enqueuedTime: T0);
        await handler.ProcessEventAsync(new ProcessEventArgs(Partition("0"), data, _ => throw new InvalidOperationException("blob down"), Ct));

        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));
        _time.Advance(FlushInterval);
        await handler.ProcessEventAsync(NoEvent("0"));

        (await TotalClicksAsync("abc1234")).ShouldBe(1);
    }

    /// <summary>An in-memory stats store that records writes and can be told to fail for a code.</summary>
    private sealed class FlakyStatsStore : IStatsStore
    {
        private readonly InMemoryStatsStore _inner = new();
        private readonly ConcurrentDictionary<string, int> _failuresLeft = new();
        private readonly ConcurrentQueue<ClickAggregate> _writes = new();
        private readonly ConcurrentQueue<string> _attempts = new();

        public IReadOnlyList<ClickAggregate> Writes => [.. _writes];

        public IReadOnlyList<string> Attempts => [.. _attempts];

        public void FailNext(string code, int times) => _failuresLeft[code] = times;

        public async Task WaitForAttemptsAsync(int count, CancellationToken cancellationToken)
        {
            while (_attempts.Count < count)
            {
                await Task.Delay(5, cancellationToken);
            }
        }

        public async Task ApplyAsync(IReadOnlyCollection<ClickAggregate> aggregates, CancellationToken cancellationToken)
        {
            foreach (var aggregate in aggregates)
            {
                _attempts.Enqueue(aggregate.Code);
                if (_failuresLeft.TryGetValue(aggregate.Code, out var left) && left > 0)
                {
                    _failuresLeft[aggregate.Code] = left - 1;
                    await Task.Yield();
                    throw new InvalidOperationException($"Store unavailable for {aggregate.Code}.");
                }
            }

            await _inner.ApplyAsync(aggregates, cancellationToken);
            foreach (var aggregate in aggregates)
            {
                _writes.Enqueue(aggregate);
            }
        }

        public Task<LinkStats?> GetAsync(string code, CancellationToken cancellationToken) => _inner.GetAsync(code, cancellationToken);

        public Task<IReadOnlyList<TopLink>> GetTopAsync(int limit, CancellationToken cancellationToken) => _inner.GetTopAsync(limit, cancellationToken);
    }
}
