using Azure.Messaging.EventHubs;

namespace UrlShortener.AnalyticsWorker;

/// <summary>
/// Runs the <see cref="EventProcessorClient"/> for the lifetime of the host. The processor balances
/// partitions across worker instances through blob leases and stores checkpoints next to them, so
/// scaling out is just running more instances.
/// </summary>
public sealed partial class ClickProcessorService(
    EventProcessorClient processor,
    ClickBatchHandler handler,
    ILogger<ClickProcessorService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        processor.PartitionInitializingAsync += handler.InitializePartitionAsync;
        processor.PartitionClosingAsync += handler.ClosePartitionAsync;
        processor.ProcessEventAsync += handler.ProcessEventAsync;
        processor.ProcessErrorAsync += handler.ProcessErrorAsync;

        await processor.StartProcessingAsync(cancellationToken).ConfigureAwait(false);
        LogStarted(processor.EventHubName, processor.ConsumerGroup);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Waits for in-flight handlers, so a batch being written finishes and checkpoints.
        await processor.StopProcessingAsync(cancellationToken).ConfigureAwait(false);
        LogStopped(processor.EventHubName);
    }

    [LoggerMessage(LogLevel.Information, "Consuming clicks from Event Hub '{EventHub}' as consumer group '{ConsumerGroup}'.")]
    private partial void LogStarted(string eventHub, string consumerGroup);

    [LoggerMessage(LogLevel.Information, "Stopped consuming clicks from Event Hub '{EventHub}'.")]
    private partial void LogStopped(string eventHub);
}
