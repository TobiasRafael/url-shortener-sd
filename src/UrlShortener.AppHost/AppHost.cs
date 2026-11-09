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

// ---- API: REST endpoints and redirects --------------------------------------------------------
builder.AddProject<Projects.UrlShortener_Api>("api")
    .WithReference(cosmos)
    .WaitFor(cosmos)
    .WithReference(cache)
    .WaitFor(cache)
    .WithEnvironment("Storage__Provider", "Cosmos")
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

builder.Build().Run();
