using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Azure.Cosmos;
using UrlShortener.Infrastructure.Cosmos;

[assembly: AssemblyFixture(typeof(UrlShortener.Infrastructure.Tests.Cosmos.CosmosEmulatorFixture))]

namespace UrlShortener.Infrastructure.Tests.Cosmos;

/// <summary>
/// Runs the Azure Cosmos DB Linux (vNext) emulator in Docker, the same image Aspire uses for
/// local development, once for the whole test assembly.
/// </summary>
/// <remarks>
/// Without Docker the Cosmos tests are <i>skipped</i>, so the suite stays runnable anywhere.
/// CI sets <c>REQUIRE_COSMOS_EMULATOR=true</c> so a broken emulator fails the build instead of
/// silently skipping. Set <c>COSMOS_EMULATOR_ENDPOINT</c> to use an emulator that's already running.
/// </remarks>
public sealed class CosmosEmulatorFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest";
    private const int GatewayPort = 8081;
    private const int HealthPort = 8080;

    /// <summary>The emulator's fixed, publicly documented key. It is not a secret.</summary>
    private const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private IContainer? _container;

    public CosmosClient? Client { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("COSMOS_EMULATOR_ENDPOINT");
            if (string.IsNullOrEmpty(endpoint))
            {
                _container = new ContainerBuilder(Image)
                    .WithEnvironment("PROTOCOL", "http")
                    .WithPortBinding(GatewayPort, assignRandomHostPort: true)
                    .WithPortBinding(HealthPort, assignRandomHostPort: true)
                    .WithWaitStrategy(Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(request => request.ForPort(HealthPort).ForPath("/ready")))
                    .Build();

                using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await _container.StartAsync(startup.Token);
                endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(GatewayPort)}/";
            }

            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                RequestTimeout = TimeSpan.FromSeconds(30),
            };
            CosmosJson.Configure(options);
            Client = new CosmosClient(endpoint, EmulatorKey, options);
        }
        catch (Exception ex) when (!IsRequired)
        {
            UnavailableReason = $"Cosmos DB emulator unavailable ({ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>A fresh database with the full schema, so every test starts from empty containers.</summary>
    public async Task<CosmosContainers> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        Assert.SkipWhen(Client is null, UnavailableReason ?? "Cosmos DB emulator unavailable");

        var name = $"test-{Guid.NewGuid():N}";
        await CosmosSchema.EnsureCreatedAsync(Client!, name, cancellationToken);
        return new CosmosContainers(Client!, name);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable("REQUIRE_COSMOS_EMULATOR"), "true", StringComparison.OrdinalIgnoreCase);
}
