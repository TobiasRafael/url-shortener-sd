using System.ComponentModel.DataAnnotations;

namespace UrlShortener.AnalyticsWorker;

public sealed class ClickConsumerOptions
{
    public const string SectionName = "ClickConsumer";

    /// <summary>
    /// Connection name of the Event Hub consumer group (<c>ConnectionStrings:{name}</c>), as Aspire
    /// injects it. The connection string carries the hub and the consumer group.
    /// </summary>
    [Required]
    public string EventHubConnectionName { get; set; } = "clicks-analytics";

    /// <summary>Connection name of the blob service that stores checkpoints and partition ownership.</summary>
    [Required]
    public string CheckpointConnectionName { get; set; } = "checkpoints";

    /// <summary>
    /// Longest a click waits in a partition's batch before it is written. It is also how long the
    /// processor waits for an event before calling the handler anyway, so a quiet partition still
    /// flushes on time.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:05:00")]
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A partition's batch is written as soon as it holds this many clicks.</summary>
    [Range(1, 1_000_000)]
    public int MaxBatchClicks { get; set; } = 20_000;

    /// <summary>Concurrent stats writes per flush (one write per link and day).</summary>
    [Range(1, 256)]
    public int MaxParallelWrites { get; set; } = 16;

    /// <summary>First wait after a failed write; it doubles on each further failure.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait between write attempts while the stats store is failing.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
