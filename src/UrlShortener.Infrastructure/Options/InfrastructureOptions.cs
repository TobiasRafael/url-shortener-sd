using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Infrastructure.Options;

public enum StorageProvider
{
    /// <summary>Process-local storage. For local runs and tests; data is lost on restart.</summary>
    InMemory,

    /// <summary>Azure Cosmos DB for NoSQL.</summary>
    Cosmos,
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.InMemory;

    public CosmosStorageOptions Cosmos { get; set; } = new();
}

public sealed class CosmosStorageOptions
{
    /// <summary>Name of the connection string (<c>ConnectionStrings:{name}</c>), as Aspire injects it.</summary>
    [Required]
    public string ConnectionName { get; set; } = "cosmos";

    [Required]
    public string DatabaseName { get; set; } = "urlshortener";

    /// <summary>
    /// Create the database and containers at startup if they don't exist. Turn this off where the
    /// app authenticates with data-plane RBAC only, which can't create containers; there,
    /// provisioning (Bicep/azd) creates them.
    /// </summary>
    public bool EnsureCreated { get; set; } = true;
}

public sealed class ShortCodeOptions
{
    public const string SectionName = "ShortCodes";

    /// <summary>
    /// Key for the Feistel permutation that scrambles counter values into codes.
    /// <b>Never change it once links exist.</b> Codes stay unique (inserts never overwrite), but
    /// the mapping from counter to code changes, so collisions with existing codes become possible.
    /// </summary>
    [Required]
    [MinLength(16)]
    public string Secret { get; set; } = string.Empty;

    /// <summary>Counter values leased per allocation. Bigger blocks mean fewer counter writes and more IDs lost on restart.</summary>
    [Range(1, 1_000_000)]
    public int BlockSize { get; set; } = 1_000;
}

public enum ClickPipeline
{
    /// <summary>Clicks are aggregated in-process and written straight to the stats store.</summary>
    Direct,

    /// <summary>Clicks are published to Azure Event Hubs and aggregated by the analytics worker.</summary>
    EventHubs,
}

public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    public ClickPipeline Pipeline { get; set; } = ClickPipeline.Direct;

    /// <summary>Connection name of the Event Hub (<c>ConnectionStrings:{name}</c>).</summary>
    [Required]
    public string EventHubConnectionName { get; set; } = "clicks";
}
