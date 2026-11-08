using System.Buffers;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using UrlShortener.Core.Links;
using UrlShortener.Core.Users;
using User = UrlShortener.Core.Users.User;

namespace UrlShortener.Infrastructure.Cosmos;

public static class CosmosContainerNames
{
    public const string Links = "links";
    public const string Destinations = "destinations";
    public const string Users = "users";
    public const string Counters = "counters";
    public const string LinkStats = "linkStats";
}

/// <summary>Shared serializer settings. The SDK, stream reads and stream writes must all agree.</summary>
public static class CosmosJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Client options every <see cref="CosmosClient"/> in the app should use.</summary>
    public static void Configure(CosmosClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UseSystemTextJsonSerializerWithOptions = Options;
        options.ApplicationName ??= "urlshortener";
    }

    internal static MemoryStream ToStream<T>(T value) => new(JsonSerializer.SerializeToUtf8Bytes(value, Options));

    internal static async Task<T> ReadAsync<T>(ResponseMessage response, CancellationToken cancellationToken)
    {
        var value = await JsonSerializer.DeserializeAsync<T>(response.Content, Options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException($"Cosmos returned an empty {typeof(T).Name} document.");
    }
}

/// <summary>Cosmos ids can't contain <c>/ \ ? #</c>. Ids that come from requests are screened before any I/O.</summary>
internal static class CosmosIds
{
    private static readonly SearchValues<char> Forbidden = SearchValues.Create("/\\?#");

    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 255 && !id.AsSpan().ContainsAny(Forbidden);
}

/// <summary>Resolves the app's containers once, from the configured database.</summary>
public sealed class CosmosContainers(CosmosClient client, string databaseName)
{
    public Container Links { get; } = client.GetContainer(databaseName, CosmosContainerNames.Links);

    public Container Destinations { get; } = client.GetContainer(databaseName, CosmosContainerNames.Destinations);

    public Container Users { get; } = client.GetContainer(databaseName, CosmosContainerNames.Users);

    public Container Counters { get; } = client.GetContainer(databaseName, CosmosContainerNames.Counters);

    public Container LinkStats { get; } = client.GetContainer(databaseName, CosmosContainerNames.LinkStats);
}

// ---- Documents ---------------------------------------------------------------------------
// Every container is partitioned by /id, so all hot-path operations are single-partition
// point reads and writes (~1 RU for a 1 KB read). Cosmos spreads the 120B-link key space
// across physical partitions automatically: that's the "sharding" the capacity estimate needs.

internal sealed record LinkDocument(string Id, string LongUrl, DateTimeOffset CreatedAt, string? UserId, bool IsCustom)
{
    public static LinkDocument From(ShortLink link) => new(link.Code, link.LongUrl, link.CreatedAt, link.UserId, link.IsCustom);

    public ShortLink ToModel() => new(Id, LongUrl, CreatedAt, UserId, IsCustom);
}

internal sealed record DestinationDocument(string Id, string Code);

internal sealed record UserDocument(string Id, string Name, string Email, DateTimeOffset CreatedAt, string ApiKeyHash)
{
    public const string UserType = "user";

    public string Type { get; init; } = UserType;

    public static UserDocument From(User user) => new(user.Id, user.Name, user.Email, user.CreatedAt, user.ApiKeyHash);

    public User ToModel() => new(Id, Name, Email, CreatedAt, ApiKeyHash);
}

/// <summary>
/// Claims an email address. It lives in the users container and its id is a hash of the email,
/// so uniqueness comes from Cosmos' own unique-id rule, with no secondary index.
/// </summary>
internal sealed record EmailClaimDocument(string Id, string UserId)
{
    public string Type { get; init; } = "email";
}

internal sealed record CounterDocument(string Id, long Next);

/// <summary>Stats for one link. <c>lastClickedAt</c> is Unix milliseconds, so conditional updates compare numbers.</summary>
internal sealed record LinkStatsDocument(string Id, long TotalClicks, long LastClickedAt, Dictionary<string, long> Daily);
