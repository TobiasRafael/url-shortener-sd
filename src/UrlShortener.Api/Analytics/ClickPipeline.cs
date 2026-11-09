using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Options;
using UrlShortener.Core.Analytics;

namespace UrlShortener.Api.Analytics;

public sealed class ApiMetrics : IDisposable
{
    public const string MeterName = "UrlShortener.Api";

    private readonly Meter _meter;

    public ApiMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);
        Redirects = _meter.CreateCounter<long>("urlshortener.redirects", description: "Short links resolved.");
        RedirectMisses = _meter.CreateCounter<long>("urlshortener.redirect_misses", description: "Lookups for codes that don't exist.");
        LinksCreated = _meter.CreateCounter<long>("urlshortener.links_created", description: "New short links.");
        ClicksDropped = _meter.CreateCounter<long>("urlshortener.clicks_dropped", description: "Clicks dropped because the buffer was full.");
        ClicksLost = _meter.CreateCounter<long>("urlshortener.clicks_lost", description: "Clicks lost because the sink failed.");
    }

    public Counter<long> Redirects { get; }

    public Counter<long> RedirectMisses { get; }

    public Counter<long> LinksCreated { get; }

    public Counter<long> ClicksDropped { get; }

    public Counter<long> ClicksLost { get; }

    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// An in-memory buffer between the redirect handler and the analytics pipeline. Enqueuing never
/// blocks or awaits. When the buffer is full the click is dropped and counted, so a slow analytics
/// backend can never slow down redirects: analytics are best-effort, redirects are not.
/// </summary>
public sealed class ClickBuffer
{
    private readonly Channel<ClickEvent> _channel;

    public ClickBuffer(IOptions<ClickBatchingOptions> options, ApiMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);

        _channel = Channel.CreateBounded<ClickEvent>(
            new BoundedChannelOptions(options.Value.Capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            },
            itemDropped: _ => metrics.ClicksDropped.Add(1));
    }

    public ChannelReader<ClickEvent> Reader => _channel.Reader;

    public void Enqueue(ClickEvent click) => _channel.Writer.TryWrite(click);
}

/// <summary>Drains the <see cref="ClickBuffer"/> on a timer and hands batches to the configured sink.</summary>
public sealed partial class ClickFlushService(
    ClickBuffer queue,
    IClickEventSink sink,
    IOptions<ClickBatchingOptions> options,
    ApiMetrics metrics,
    TimeProvider timeProvider,
    ILogger<ClickFlushService> logger) : BackgroundService
{
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.FlushInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: flush whatever is buffered, within a bounded time.
            using var timeout = new CancellationTokenSource(ShutdownFlushTimeout);
            await FlushAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        var maxBatch = options.Value.MaxBatchSize;
        var batch = new List<ClickEvent>(Math.Min(maxBatch, 1_024));

        while (queue.Reader.TryRead(out var click))
        {
            batch.Add(click);
            if (batch.Count == maxBatch)
            {
                await PublishAsync(batch, cancellationToken).ConfigureAwait(false);
                batch = new List<ClickEvent>(Math.Min(maxBatch, 1_024));
            }
        }

        if (batch.Count > 0)
        {
            await PublishAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(List<ClickEvent> batch, CancellationToken cancellationToken)
    {
        try
        {
            await sink.PublishAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Analytics are best-effort: record the loss and keep serving.
            metrics.ClicksLost.Add(batch.Count);
            LogPublishFailed(ex, batch.Count);
        }
    }

    [LoggerMessage(LogLevel.Error, "Failed to publish {Count} clicks; they are lost.")]
    private partial void LogPublishFailed(Exception exception, int count);
}
