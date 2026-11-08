using System.Collections.ObjectModel;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Infrastructure.Options;

namespace UrlShortener.Infrastructure.Cosmos;

/// <summary>Creates the database and containers (with indexing policies tuned to the queries).</summary>
public static class CosmosSchema
{
    public static async Task EnsureCreatedAsync(CosmosClient client, string databaseName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var database = (await client.CreateDatabaseIfNotExistsAsync(databaseName, cancellationToken: cancellationToken).ConfigureAwait(false)).Database;

        foreach (var properties in ContainerDefinitions())
        {
            await database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public static IEnumerable<ContainerProperties> ContainerDefinitions()
    {
        // links: only "my links" queries filter on userId and sort by createdAt.
        yield return Container(CosmosContainerNames.Links, includedPaths: ["/userId/?", "/createdAt/?"]);

        // destinations, users, counters: point reads only, so nothing is indexed and writes stay cheap.
        yield return Container(CosmosContainerNames.Destinations, includedPaths: []);
        yield return Container(CosmosContainerNames.Users, includedPaths: []);
        yield return Container(CosmosContainerNames.Counters, includedPaths: []);

        // linkStats: "top links" sorts by totalClicks. The per-day map grows forever, so it is not indexed.
        yield return Container(CosmosContainerNames.LinkStats, includedPaths: ["/totalClicks/?", "/lastClickedAt/?"]);
    }

    private static ContainerProperties Container(string name, IReadOnlyList<string> includedPaths)
    {
        var properties = new ContainerProperties(name, partitionKeyPath: "/id");
        properties.IndexingPolicy.IncludedPaths.Clear();
        foreach (var path in includedPaths)
        {
            properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
        }
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
        return properties;
    }
}

/// <summary>Runs <see cref="CosmosSchema.EnsureCreatedAsync"/> at startup, before the app serves traffic.</summary>
public sealed partial class CosmosInitializer(
    CosmosClient client,
    IOptions<StorageOptions> options,
    ILogger<CosmosInitializer> logger) : IHostedService
{
    private static readonly ReadOnlyCollection<TimeSpan> RetryDelays =
        Array.AsReadOnly([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)]);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cosmos = options.Value.Cosmos;
        if (!cosmos.EnsureCreated)
        {
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await CosmosSchema.EnsureCreatedAsync(client, cosmos.DatabaseName, cancellationToken).ConfigureAwait(false);
                LogSchemaReady(cosmos.DatabaseName);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                // Data-plane RBAC can't create resources; provisioning owns the schema in that case.
                LogSchemaForbidden(cosmos.DatabaseName);
                return;
            }
            catch (Exception ex) when (attempt < RetryDelays.Count && ex is CosmosException or HttpRequestException)
            {
                LogSchemaRetry(ex, attempt + 1);
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(LogLevel.Information, "Cosmos DB schema ready in database '{Database}'.")]
    private partial void LogSchemaReady(string database);

    [LoggerMessage(LogLevel.Warning, "Not allowed to create Cosmos DB resources in '{Database}'; assuming they were provisioned.")]
    private partial void LogSchemaForbidden(string database);

    [LoggerMessage(LogLevel.Warning, "Cosmos DB not reachable yet (attempt {Attempt}); retrying.")]
    private partial void LogSchemaRetry(Exception exception, int attempt);
}
