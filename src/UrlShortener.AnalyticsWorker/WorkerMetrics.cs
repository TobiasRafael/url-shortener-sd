using System.Diagnostics.Metrics;

namespace UrlShortener.AnalyticsWorker;

public sealed class WorkerMetrics : IDisposable
{
    /// <summary>The application name, so ServiceDefaults exports it without extra wiring.</summary>
    public const string MeterName = "UrlShortener.AnalyticsWorker";

    private readonly Meter _meter;

    public WorkerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);
        ClicksApplied = _meter.CreateCounter<long>("urlshortener.clicks_applied", description: "Clicks written to the stats store.");
        EventsRejected = _meter.CreateCounter<long>("urlshortener.click_events_rejected", description: "Events that could not be read and were skipped.");
        WriteFailures = _meter.CreateCounter<long>("urlshortener.stats_write_failures", description: "Failed attempts to write a batch of stats; the batch is retried.");
        CheckpointFailures = _meter.CreateCounter<long>("urlshortener.checkpoint_failures", description: "Checkpoints that could not be saved.");
    }

    public Counter<long> ClicksApplied { get; }

    public Counter<long> EventsRejected { get; }

    public Counter<long> WriteFailures { get; }

    public Counter<long> CheckpointFailures { get; }

    public void Dispose() => _meter.Dispose();
}
