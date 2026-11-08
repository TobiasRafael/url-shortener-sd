namespace UrlShortener.Core.Analytics;

/// <summary>A single redirect. Kept deliberately small, because there are about 8,000 of these per second.</summary>
public readonly record struct ClickEvent(string Code, DateTimeOffset Timestamp);

/// <summary>Clicks for one link on one UTC day, merged from many <see cref="ClickEvent"/>s.</summary>
public readonly record struct ClickAggregate(string Code, DateOnly Day, long Count, DateTimeOffset LastClickedAt);

public sealed record LinkStats(
    string Code,
    long TotalClicks,
    DateTimeOffset? LastClickedAt,
    IReadOnlyDictionary<DateOnly, long> Daily)
{
    public static LinkStats Empty(string code) => new(code, 0, null, new Dictionary<DateOnly, long>());
}

public sealed record TopLink(string Code, long TotalClicks, DateTimeOffset? LastClickedAt);

/// <summary>The durable store for click counters.</summary>
public interface IStatsStore
{
    /// <summary>
    /// Adds the aggregates to the stored counters. Increments are commutative, so batches can be
    /// applied in any order and by any number of consumers at once.
    /// </summary>
    Task ApplyAsync(IReadOnlyCollection<ClickAggregate> aggregates, CancellationToken cancellationToken);

    Task<LinkStats?> GetAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<TopLink>> GetTopAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>Where the API sends batches of clicks: straight to the stats store, or to Event Hubs.</summary>
public interface IClickEventSink
{
    Task PublishAsync(IReadOnlyList<ClickEvent> clicks, CancellationToken cancellationToken);
}

public static class ClickAggregator
{
    /// <summary>
    /// Merges a batch of raw clicks into one counter increment per (link, day). One
    /// write per pair, instead of one per click, is what keeps the database write rate
    /// far below the redirect rate.
    /// </summary>
    public static IReadOnlyCollection<ClickAggregate> Aggregate(IEnumerable<ClickEvent> clicks)
    {
        ArgumentNullException.ThrowIfNull(clicks);

        var buckets = new Dictionary<(string Code, DateOnly Day), ClickAggregate>();
        foreach (var click in clicks)
        {
            var day = DateOnly.FromDateTime(click.Timestamp.UtcDateTime);
            var key = (click.Code, day);

            buckets[key] = buckets.TryGetValue(key, out var existing)
                ? existing with
                {
                    Count = existing.Count + 1,
                    LastClickedAt = click.Timestamp > existing.LastClickedAt ? click.Timestamp : existing.LastClickedAt,
                }
                : new ClickAggregate(click.Code, day, 1, click.Timestamp);
        }
        return buckets.Values;
    }
}
