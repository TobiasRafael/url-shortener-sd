using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.AnalyticsWorker;

namespace Microsoft.Extensions.Hosting;

public static class WorkerHostingExtensions
{
    /// <summary>
    /// Registers the Event Hubs click consumer: the processor client (with blob checkpoints), the
    /// batching handler and the hosted service that runs them. The stats store comes from
    /// <c>AddUrlShortenerStorage</c>.
    /// </summary>
    public static IHostApplicationBuilder AddClickConsumer(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(ClickConsumerOptions.SectionName);
        builder.Services.AddOptions<ClickConsumerOptions>().Bind(section).ValidateDataAnnotations().ValidateOnStart();
        var options = section.Get<ClickConsumerOptions>() ?? new ClickConsumerOptions();

        // The processor takes the un-keyed BlobServiceClient for checkpoints and leases, and creates
        // a container named after the namespace, hub and consumer group.
        builder.AddAzureBlobServiceClient(options.CheckpointConnectionName);
        builder.AddAzureEventProcessorClient(
            options.EventHubConnectionName,
            configureClientBuilder: client => client.ConfigureOptions(processor =>
                // Call the handler even when nothing arrives, so a quiet partition flushes on time.
                processor.MaximumWaitTime = options.FlushInterval));

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<WorkerMetrics>();
        builder.Services.AddSingleton<ClickBatchHandler>();
        builder.Services.AddHostedService<ClickProcessorService>();

        return builder;
    }
}
