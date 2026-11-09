using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Options;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace UrlShortener.Api.Tests.Unit;

public sealed class LinkCacheTests : IDisposable
{
    private static readonly ShortLink Link = new("abc1234", "https://example.com/", DateTimeOffset.UnixEpoch, null, false);

    private readonly ServiceProvider _services = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
    private readonly ILinkRepository _repository = Substitute.For<ILinkRepository>();
    private readonly LinkCache _cache;

    public LinkCacheTests() => _cache = new LinkCache(_services.GetRequiredService<HybridCache>(), _repository);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Repeated_lookups_hit_the_store_once()
    {
        _repository.GetAsync("abc1234", Arg.Any<CancellationToken>()).Returns(Link);

        for (var i = 0; i < 5; i++)
        {
            (await _cache.GetAsync("abc1234", Ct)).ShouldBe(Link);
        }

        await _repository.Received(1).GetAsync("abc1234", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_burst_of_concurrent_misses_makes_one_store_call()
    {
        var gate = new TaskCompletionSource<ShortLink?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _repository.GetAsync("abc1234", Arg.Any<CancellationToken>()).Returns(gate.Task);

        var lookups = Enumerable.Range(0, 20).Select(_ => _cache.GetAsync("abc1234", Ct).AsTask()).ToArray();
        gate.SetResult(Link);

        (await Task.WhenAll(lookups)).ShouldAllBe(l => l == Link);
        await _repository.Received(1).GetAsync("abc1234", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Misses_are_cached_too_so_probing_bots_cannot_hammer_the_store()
    {
        _repository.GetAsync("nope123", Arg.Any<CancellationToken>()).Returns((ShortLink?)null);

        for (var i = 0; i < 5; i++)
        {
            (await _cache.GetAsync("nope123", Ct)).ShouldBeNull();
        }

        await _repository.Received(1).GetAsync("nope123", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forgetting_a_cached_miss_lets_a_newly_created_code_resolve()
    {
        _repository.GetAsync("promo42", Arg.Any<CancellationToken>()).Returns((ShortLink?)null, Link with { Code = "promo42" });

        (await _cache.GetAsync("promo42", Ct)).ShouldBeNull();
        await _cache.ForgetAsync("promo42", Ct);

        (await _cache.GetAsync("promo42", Ct)).ShouldNotBeNull().Code.ShouldBe("promo42");
    }

    [Fact]
    public void Misses_expire_much_sooner_than_hits()
    {
        LinkCache.Missing.Expiration!.Value.ShouldBeLessThan(TimeSpan.FromMinutes(1));
        LinkCache.Found.Expiration!.Value.ShouldBeGreaterThan(TimeSpan.FromDays(1));
    }
}

public sealed class ClickPipelineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly ApiMetrics _metrics;

    public ClickPipelineTests() => _metrics = new ApiMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _metrics.Dispose();
        _services.Dispose();
    }

    private ClickBuffer Buffer(int capacity) => new(MsOptions.Create(new ClickBatchingOptions { Capacity = capacity }), _metrics);

    private ClickFlushService FlushService(ClickBuffer buffer, IClickEventSink sink, int maxBatch = 10_000) => new(
        buffer,
        sink,
        MsOptions.Create(new ClickBatchingOptions { MaxBatchSize = maxBatch }),
        _metrics,
        TimeProvider.System,
        NullLogger<ClickFlushService>.Instance);

    [Fact]
    public void A_full_buffer_drops_new_clicks_instead_of_blocking_the_redirect()
    {
        var buffer = Buffer(capacity: 3);

        for (var i = 0; i < 10; i++)
        {
            buffer.Enqueue(new ClickEvent("abc1234", T0)); // returns immediately every time
        }

        buffer.Reader.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Flushing_drains_the_buffer_in_batches_of_at_most_max_size()
    {
        var buffer = Buffer(capacity: 100);
        var sink = Substitute.For<IClickEventSink>();
        var batchSizes = new List<int>();
        sink.PublishAsync(Arg.Do<IReadOnlyList<ClickEvent>>(b => batchSizes.Add(b.Count)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        for (var i = 0; i < 25; i++)
        {
            buffer.Enqueue(new ClickEvent($"code{i}", T0));
        }

        await FlushService(buffer, sink, maxBatch: 10).FlushAsync(Ct);

        batchSizes.ShouldBe([10, 10, 5]);
        buffer.Reader.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_sink_loses_that_batch_but_never_throws()
    {
        var buffer = Buffer(capacity: 10);
        var sink = Substitute.For<IClickEventSink>();
        sink.PublishAsync(Arg.Any<IReadOnlyList<ClickEvent>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("sink down"));
        buffer.Enqueue(new ClickEvent("abc1234", T0));

        await Should.NotThrowAsync(() => FlushService(buffer, sink).FlushAsync(Ct));

        buffer.Reader.Count.ShouldBe(0);
    }

    [Fact]
    public async Task An_empty_buffer_makes_no_sink_calls()
    {
        var sink = Substitute.For<IClickEventSink>();

        await FlushService(Buffer(10), sink).FlushAsync(Ct);

        await sink.DidNotReceive().PublishAsync(Arg.Any<IReadOnlyList<ClickEvent>>(), Arg.Any<CancellationToken>());
    }
}
