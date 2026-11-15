using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Locally the Cosmos emulator keeps its container and data between runs, because it takes a while
// to start and losing your links on every F5 is annoying. Integration tests turn this off so every
// test run starts clean and leaves nothing behind.
var persistentContainers = builder.Configuration.GetValue("AppHost:PersistentContainers", defaultValue: true);

// The Feistel key that scrambles counter values into short codes. It has to stay the same for the
// lifetime of the service, so it is generated once and persisted (user secrets locally; a secure
// deployment parameter in Azure) instead of living in appsettings.
var shortCodeSecret = builder.AddParameter(
    "shortcode-secret",
    new GenerateParameterDefault { MinLength = 48, Special = false },
    secret: true,
    persist: true);

// ---- Data: Cosmos DB for NoSQL ----------------------------------------------------------------
// Locally this is the Linux (vNext) emulator. The API's CosmosInitializer creates the database and
// containers with their tuned indexing policies, so they are not declared here: Aspire would create
// them first with the default index-everything policy.
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator(emulator =>
    {
        emulator.WithDataExplorer();
        if (persistentContainers)
        {
            emulator.WithLifetime(ContainerLifetime.Persistent).WithDataVolume();
        }
    });

// ---- Cache: Redis as the shared L2 behind each instance's in-memory L1 ------------------------
var cache = builder.AddAzureManagedRedis("cache")
    .WithAccessKeyAuthentication()
    .RunAsContainer();

// ---- Click stream: the API publishes clicks to Event Hubs, the analytics worker consumes them --
// The emulator and the checkpoint storage are throwaway even locally. Checkpoints only make sense
// together with the hub they point into, and clicks in flight are not worth keeping between runs;
// the stats they turn into live in Cosmos, which does persist.
var eventHubs = builder.AddAzureEventHubs("eventhubs").RunAsEmulator();
var clicks = eventHubs.AddHub("clicks");

// A consumer group of its own, so other readers (an archive, a fraud check) can be added later
// without sharing the worker's checkpoints.
var analyticsClicks = clicks.AddConsumerGroup("clicks-analytics", "analytics");

// Blob storage for the processor's checkpoints and partition leases.
var checkpoints = builder.AddAzureStorage("storage")
    .RunAsEmulator()
    .AddBlobs("checkpoints");

// ---- API: REST endpoints and redirects --------------------------------------------------------
builder.AddProject<Projects.UrlShortener_Api>("api")
    .WithReference(cosmos)
    .WaitFor(cosmos)
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(clicks)
    .WaitFor(eventHubs)
    .WithEnvironment("Storage__Provider", "Cosmos")
    .WithEnvironment("Analytics__Pipeline", "EventHubs")
    .WithEnvironment("ShortCodes__Secret", shortCodeSecret)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    .WithUrls(context =>
    {
        foreach (var url in context.Urls.Where(u => u.Endpoint is not null).ToList())
        {
            context.Urls.Add(new ResourceUrlAnnotation
            {
                Url = $"{url.Url.TrimEnd('/')}/scalar",
                DisplayText = $"API docs ({url.Endpoint!.EndpointName})",
                Endpoint = url.Endpoint,
            });
        }
    });

// ---- Analytics worker: aggregates clicks into Cosmos stats ------------------------------------
// No endpoints: it only reads from Event Hubs and writes to Cosmos. Scaling out is running more
// instances; the processor spreads the hub's partitions across them.
builder.AddProject<Projects.UrlShortener_AnalyticsWorker>("analytics-worker")
    .WithReference(analyticsClicks)
    .WaitFor(eventHubs)
    .WithReference(checkpoints)
    .WaitFor(checkpoints)
    .WithReference(cosmos)
    .WaitFor(cosmos)
    .WithEnvironment("Storage__Provider", "Cosmos");

builder.Build().Run();
