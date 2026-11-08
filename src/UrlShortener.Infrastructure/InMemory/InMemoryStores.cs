using System.Collections.Concurrent;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Users;

namespace UrlShortener.Infrastructure.InMemory;

// Process-local implementations for running locally with no dependencies, and for tests.
// They follow the same contracts as the Cosmos implementations; a shared contract test
// suite checks both.

public sealed class InMemoryLinkRepository : ILinkRepository
{
    private readonly ConcurrentDictionary<string, ShortLink> _links = new(StringComparer.Ordinal);

    public Task<bool> TryCreateAsync(ShortLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Task.FromResult(_links.TryAdd(link.Code, link));
    }

    public Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(_links.GetValueOrDefault(code));

    public Task<IReadOnlyList<ShortLink>> GetByUserAsync(string userId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShortLink>>(
        [
            .. _links.Values
                .Where(l => string.Equals(l.UserId, userId, StringComparison.Ordinal))
                .OrderByDescending(l => l.CreatedAt)
                .Take(limit),
        ]);
}

public sealed class InMemoryDestinationIndex : IDestinationIndex
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

    public Task<string?> FindCodeAsync(string destinationKey, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.GetValueOrDefault(destinationKey));

    public Task<string> AddIfAbsentAsync(string destinationKey, string code, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.GetOrAdd(destinationKey, code));
}

public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<string, User> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _emails = new(StringComparer.Ordinal);

    public Task<bool> TryCreateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!_emails.TryAdd(user.Email, user.Id))
        {
            return Task.FromResult(false);
        }
        if (!_users.TryAdd(user.Id, user))
        {
            _emails.TryRemove(user.Email, out _);
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    public Task<User?> GetAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_users.GetValueOrDefault(id));
}

public sealed class InMemoryIdRangeAllocator : IIdRangeAllocator
{
    private long _next;

    public Task<IdRange> AllocateAsync(int size, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var end = Interlocked.Add(ref _next, size);
        return Task.FromResult(new IdRange(end - size, end));
    }
}

public sealed class InMemoryStatsStore : IStatsStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task ApplyAsync(IReadOnlyCollection<ClickAggregate> aggregates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregates);

        foreach (var aggregate in aggregates)
        {
            var entry = _entries.GetOrAdd(aggregate.Code, static _ => new Entry());
            lock (entry)
            {
                entry.Total += aggregate.Count;
                entry.Daily[aggregate.Day] = entry.Daily.GetValueOrDefault(aggregate.Day) + aggregate.Count;
                if (entry.LastClickedAt is null || aggregate.LastClickedAt > entry.LastClickedAt)
                {
                    entry.LastClickedAt = aggregate.LastClickedAt;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task<LinkStats?> GetAsync(string code, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(code, out var entry))
        {
            return Task.FromResult<LinkStats?>(null);
        }

        lock (entry)
        {
            return Task.FromResult<LinkStats?>(
                new LinkStats(code, entry.Total, entry.LastClickedAt, new Dictionary<DateOnly, long>(entry.Daily)));
        }
    }

    public Task<IReadOnlyList<TopLink>> GetTopAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var snapshot = _entries.Select(pair =>
        {
            lock (pair.Value)
            {
                return new TopLink(pair.Key, pair.Value.Total, pair.Value.LastClickedAt);
            }
        });

        return Task.FromResult<IReadOnlyList<TopLink>>(
            [.. snapshot.OrderByDescending(t => t.TotalClicks).ThenBy(t => t.Code, StringComparer.Ordinal).Take(limit)]);
    }

    private sealed class Entry
    {
        public long Total { get; set; }

        public DateTimeOffset? LastClickedAt { get; set; }

        public Dictionary<DateOnly, long> Daily { get; } = [];
    }
}
