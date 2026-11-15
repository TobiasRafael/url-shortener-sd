using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.EventHubs.Processor;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;
using UrlShortener.Infrastructure.Analytics;

namespace UrlShortener.AnalyticsWorker;

/// <summary>
/// Turns the click stream into stats writes. Each partition collects clicks for up to
/// <see cref="ClickConsumerOptions.FlushInterval"/>, then writes one increment per (link, day) and
/// checkpoints the last event it covered. Batching across events is what keeps the database write
/// rate far below the click rate.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is the safety argument: stats are written first, the checkpoint second. Clicks that
/// are buffered but not yet written are never checkpointed either, so when a partition moves to
/// another instance (or the worker restarts) the new owner replays them from the last checkpoint
/// and nothing is lost.
/// </para>
/// <para>
/// Delivery is at least once. A crash between writing and checkpointing replays a batch that was
/// already counted, so totals can come out slightly high, never low. That is the usual trade-off for
/// click analytics, and it keeps the hot path free of de-duplication state.
/// </para>
/// <para>
/// The processor calls the handler one event at a time per partition, and different partitions
/// in parallel. A failing database makes the handler retry in place, which stalls only that
/// partition: Event Hubs holds the backlog, so nothing piles up in memory.
/// </para>
/// </remarks>
public sealed partial class ClickBatchHandler(
    IStatsStore store,
    IOptions<ClickConsumerOptions> options,
    WorkerMetrics metrics,
    TimeProvider timeProvider,
    ILogger<ClickBatchHandler> logger)
{
    private readonly ConcurrentDictionary<string, PartitionBatch> _partitions = new();

    public Task InitializePartitionAsync(PartitionInitializingEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        _partitions[args.PartitionId] = new PartitionBatch();
        return Task.CompletedTask;
    }

    public Task ClosePartitionAsync(PartitionClosingEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Whatever is still buffered was neither written nor checkpointed, so the next owner of the
        // partition replays it. Dropping it here loses nothing and counts nothing twice.
        if (_partitions.TryRemove(args.PartitionId, out var batch) && batch.HasUncheckpointedEvents)
        {
            LogBatchLeftForReplay(args.PartitionId, batch.Clicks.Count, args.Reason);
        }
        return Task.CompletedTask;
    }

    public async Task ProcessEventAsync(ProcessEventArgs args)
    {
        var partitionId = args.Partition.PartitionId;
        var batch = _partitions.GetOrAdd(partitionId, static _ => new PartitionBatch());
        var now = timeProvider.GetUtcNow();

        // Without an event this is the processor's "nothing arrived for MaximumWaitTime" call,
        // which is what lets a quiet partition still flush on time.
        if (args.HasEvent)
        {
            batch.Add(args, Read(args), now);
        }

        if (!batch.IsDue(now, options.Value))
        {
            return;
        }

        try
        {
            await FlushAsync(partitionId, batch, args.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested)
        {
            // Stopping, or the partition was lost while the database was failing. The batch was not
            // checkpointed, so the next owner replays it.
        }
    }

    public Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        // Errors the processor recovers from by itself (lost connections, ownership changes);
        // logging them is all there is to do.
        LogProcessorError(args.Exception, args.Operation, args.PartitionId ?? "-");
        return Task.CompletedTask;
    }

    private List<ClickEvent> Read(ProcessEventArgs args)
    {
        IReadOnlyList<ClickEvent> clicks;
        try
        {
            clicks = ClickEventSerializer.Deserialize(args.Data.EventBody);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Retrying can't fix a malformed event, and waiting on it would stall the partition.
            // It is skipped, and the checkpoint moves past it with the rest of the batch.
            metrics.EventsRejected.Add(1);
            LogEventRejected(ex, args.Partition.PartitionId, args.Data.SequenceNumber);
            return [];
        }

        // A code that could never be a link would fail its write on every retry and block the
        // partition, so it is dropped here instead.
        var valid = clicks.Where(c => LinkRules.IsValidCodeFormat(c.Code)).ToList();
        if (valid.Count != clicks.Count)
        {
            LogInvalidCodesDropped(clicks.Count - valid.Count, args.Partition.PartitionId, args.Data.SequenceNumber);
        }
        return valid;
    }

    private async Task FlushAsync(string partitionId, PartitionBatch batch, CancellationToken stopping)
    {
        var settings = options.Value;
        var pending = ClickAggregator.Aggregate(batch.Clicks).ToList();
        var delay = settings.InitialRetryDelay;

        while (pending.Count > 0)
        {
            var (failed, error) = await WriteAsync(pending, settings.MaxParallelWrites).ConfigureAwait(false);
            if (failed.Count == 0)
            {
                break;
            }

            // Only the writes that failed are retried: increments are not idempotent, so writing the
            // others again would count them twice.
            metrics.WriteFailures.Add(1);
            LogWriteFailed(error, partitionId, failed.Count, pending.Count, delay);
            pending = failed;

            await Task.Delay(delay, timeProvider, stopping).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, settings.MaxRetryDelay.Ticks));
        }

        metrics.ClicksApplied.Add(batch.Clicks.Count);
        await CheckpointAsync(partitionId, batch).ConfigureAwait(false);
        batch.Reset();
    }

    /// <summary>
    /// One call per aggregate, so a failure leaves behind exactly the aggregates that were not
    /// written. A write that has started is not cancelled: abandoning it halfway is what would
    /// turn a shutdown into double counting.
    /// </summary>
    private async Task<(List<ClickAggregate> Failed, Exception? Error)> WriteAsync(List<ClickAggregate> aggregates, int maxParallelWrites)
    {
        var failed = new ConcurrentBag<ClickAggregate>();
        Exception? firstError = null;

        await Parallel.ForEachAsync(
            aggregates,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelWrites },
            async (aggregate, token) =>
            {
                try
                {
                    await store.ApplyAsync([aggregate], token).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // any failure means "retry this aggregate later"; the first one is logged
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failed.Add(aggregate);
                    Interlocked.CompareExchange(ref firstError, ex, null);
                }
            }).ConfigureAwait(false);

        return ([.. failed], firstError);
    }

    private async Task CheckpointAsync(string partitionId, PartitionBatch batch)
    {
        try
        {
            // The checkpoint for the newest event in the batch covers every event before it.
            await batch.LastEvent!.Value.UpdateCheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the stats are already written; the next checkpoint covers this batch too
        catch (Exception ex)
#pragma warning restore CA1031
        {
            metrics.CheckpointFailures.Add(1);
            LogCheckpointFailed(ex, partitionId);
        }
    }

    [LoggerMessage(LogLevel.Information, "Partition {PartitionId} closed ({Reason}) with {Count} unwritten clicks; they will be replayed from the last checkpoint.")]
    private partial void LogBatchLeftForReplay(string partitionId, int count, ProcessingStoppedReason reason);

    [LoggerMessage(LogLevel.Warning, "Event processor error during {Operation} on partition {PartitionId}.")]
    private partial void LogProcessorError(Exception exception, string operation, string partitionId);

    [LoggerMessage(LogLevel.Error, "Skipped unreadable click event {SequenceNumber} on partition {PartitionId}.")]
    private partial void LogEventRejected(Exception exception, string partitionId, long sequenceNumber);

    [LoggerMessage(LogLevel.Warning, "Dropped {Count} clicks with invalid codes from event {SequenceNumber} on partition {PartitionId}.")]
    private partial void LogInvalidCodesDropped(int count, string partitionId, long sequenceNumber);

    [LoggerMessage(LogLevel.Error, "Writing stats for partition {PartitionId} failed for {Failed} of {Total} links; retrying in {Delay}.")]
    private partial void LogWriteFailed(Exception? exception, string partitionId, int failed, int total, TimeSpan delay);

    [LoggerMessage(LogLevel.Warning, "Could not checkpoint partition {PartitionId}; its stats are written and the next checkpoint covers them.")]
    private partial void LogCheckpointFailed(Exception exception, string partitionId);

    /// <summary>Clicks read from one partition since its last checkpoint.</summary>
    private sealed class PartitionBatch
    {
        private DateTimeOffset _openedAt;

        public List<ClickEvent> Clicks { get; } = [];

        /// <summary>The newest event read; checkpointing it covers the whole batch.</summary>
        public ProcessEventArgs? LastEvent { get; private set; }

        /// <summary>
        /// True even for a batch with no clicks (only skipped events), so the checkpoint still moves
        /// past them and they are not read again.
        /// </summary>
        public bool HasUncheckpointedEvents => LastEvent is not null;

        public void Add(ProcessEventArgs args, List<ClickEvent> clicks, DateTimeOffset now)
        {
            if (LastEvent is null)
            {
                _openedAt = now;
            }
            LastEvent = args;
            Clicks.AddRange(clicks);
        }

        public bool IsDue(DateTimeOffset now, ClickConsumerOptions settings) =>
            HasUncheckpointedEvents && (Clicks.Count >= settings.MaxBatchClicks || now - _openedAt >= settings.FlushInterval);

        public void Reset()
        {
            Clicks.Clear();
            LastEvent = null;
        }
    }
}
