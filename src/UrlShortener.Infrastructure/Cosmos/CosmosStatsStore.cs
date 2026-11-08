using System.Globalization;
using System.Net;
using Microsoft.Azure.Cosmos;
using UrlShortener.Core.Analytics;

namespace UrlShortener.Infrastructure.Cosmos;

/// <summary>
/// Click counters, updated with server-side patch increments. Increments add up in any order, so
/// any number of consumers can apply batches at once with no read-modify-write and no locking.
/// </summary>
public sealed class CosmosStatsStore(CosmosContainers containers) : IStatsStore
{
    private const string DayFormat = "yyyy-MM-dd";
    private const int MaxParallelWrites = 16;
    private const int MaxAttempts = 4;

    public Task ApplyAsync(IReadOnlyCollection<ClickAggregate> aggregates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregates);

        return Parallel.ForEachAsync(
            aggregates,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelWrites, CancellationToken = cancellationToken },
            async (aggregate, token) => await ApplyOneAsync(aggregate, token).ConfigureAwait(false));
    }

    public async Task<LinkStats?> GetAsync(string code, CancellationToken cancellationToken)
    {
        if (!CosmosIds.IsValid(code))
        {
            return null;
        }

        using var response = await containers.LinkStats
            .ReadItemStreamAsync(code, new PartitionKey(code), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        var document = await CosmosJson.ReadAsync<LinkStatsDocument>(response, cancellationToken).ConfigureAwait(false);
        var daily = document.Daily.ToDictionary(
            pair => DateOnly.ParseExact(pair.Key, DayFormat, CultureInfo.InvariantCulture),
            pair => pair.Value);

        return new LinkStats(document.Id, document.TotalClicks, FromUnixMs(document.LastClickedAt), daily);
    }

    public async Task<IReadOnlyList<TopLink>> GetTopAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Each physical partition returns its own top N from the totalClicks index and the SDK
        // merges them, so this stays cheap however many links exist. The API caches the result.
        var query = new QueryDefinition(
            $"SELECT TOP {limit.ToString(CultureInfo.InvariantCulture)} c.id, c.totalClicks, c.lastClickedAt FROM c ORDER BY c.totalClicks DESC");

        var top = new List<TopLink>(limit);
        using var iterator = containers.LinkStats.GetItemQueryIterator<TopLinkRow>(query, requestOptions: new QueryRequestOptions { MaxItemCount = limit });
        while (iterator.HasMoreResults && top.Count < limit)
        {
            foreach (var row in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                top.Add(new TopLink(row.Id, row.TotalClicks, FromUnixMs(row.LastClickedAt)));
            }
        }
        return top;
    }

    private async Task ApplyOneAsync(ClickAggregate aggregate, CancellationToken cancellationToken)
    {
        var partitionKey = new PartitionKey(aggregate.Code);
        var lastClickedMs = aggregate.LastClickedAt.ToUnixTimeMilliseconds();
        var day = aggregate.Day.ToString(DayFormat, CultureInfo.InvariantCulture);

        PatchOperation[] increments =
        [
            PatchOperation.Increment("/totalClicks", aggregate.Count),
            PatchOperation.Increment($"/daily/{day}", aggregate.Count),
        ];

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // Usual case, one write: increment and move lastClickedAt forward, but only if this
            // batch is newer. An older, late batch fails the predicate (412) and falls back to
            // increment-only, so lastClickedAt never goes backwards.
            using (var response = await containers.LinkStats.PatchItemStreamAsync(
                aggregate.Code,
                partitionKey,
                [.. increments, PatchOperation.Set("/lastClickedAt", lastClickedMs)],
                new PatchItemRequestOptions
                {
                    FilterPredicate = $"FROM c WHERE c.lastClickedAt < {lastClickedMs.ToString(CultureInfo.InvariantCulture)}",
                },
                cancellationToken).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    using var incrementOnly = await containers.LinkStats
                        .PatchItemStreamAsync(aggregate.Code, partitionKey, increments, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    incrementOnly.EnsureSuccessStatusCode();
                    return;
                }

                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    response.EnsureSuccessStatusCode();
                }
            }

            // First click for this link: create the document. If a concurrent consumer created it
            // first (409), go round again and patch it.
            var document = new LinkStatsDocument(aggregate.Code, aggregate.Count, lastClickedMs, new Dictionary<string, long> { [day] = aggregate.Count });
            using var body = CosmosJson.ToStream(document);
            using var created = await containers.LinkStats
                .CreateItemStreamAsync(body, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (created.IsSuccessStatusCode)
            {
                return;
            }
            if (created.StatusCode != HttpStatusCode.Conflict)
            {
                created.EnsureSuccessStatusCode();
            }
        }

        throw new InvalidOperationException($"Could not apply click stats for '{aggregate.Code}'.");
    }

    private static DateTimeOffset? FromUnixMs(long value) => value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;

    private sealed record TopLinkRow(string Id, long TotalClicks, long LastClickedAt);
}
