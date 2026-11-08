using System.Collections.Concurrent;
using UrlShortener.Core.Links;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Users;

namespace UrlShortener.Core.Tests.TestDoubles;

// Thread-safe hand-written fakes. The concurrency tests need real atomic insert-if-absent
// behaviour, which a mocking library can't reproduce. Every await yields first, so racing
// callers really do interleave.

internal sealed class FakeLinkRepository : ILinkRepository
{
    private readonly ConcurrentDictionary<string, ShortLink> _links = new(StringComparer.Ordinal);

    public int Count => _links.Count;

    public void Seed(ShortLink link) => _links[link.Code] = link;

    public async Task<bool> TryCreateAsync(ShortLink link, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return _links.TryAdd(link.Code, link);
    }

    public Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(_links.GetValueOrDefault(code));

    public Task<IReadOnlyList<ShortLink>> GetByUserAsync(string userId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShortLink>>(
            [.. _links.Values.Where(l => l.UserId == userId).OrderByDescending(l => l.CreatedAt).Take(limit)]);
}

internal sealed class FakeDestinationIndex : IDestinationIndex
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public void Seed(string destinationKey, string code) => _entries[destinationKey] = code;

    public Task<string?> FindCodeAsync(string destinationKey, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.GetValueOrDefault(destinationKey));

    public async Task<string> AddIfAbsentAsync(string destinationKey, string code, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return _entries.GetOrAdd(destinationKey, code);
    }
}

internal sealed class FakeUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<string, User> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _emails = new(StringComparer.Ordinal);

    public Task<bool> TryCreateAsync(User user, CancellationToken cancellationToken)
    {
        if (!_emails.TryAdd(user.Email, user.Id))
        {
            return Task.FromResult(false);
        }
        _users[user.Id] = user;
        return Task.FromResult(true);
    }

    public Task<User?> GetAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_users.GetValueOrDefault(id));
}

/// <summary>A central counter, like the real ticket server: blocks are handed out atomically.</summary>
internal sealed class SequentialIdAllocator(long start = 0) : IIdRangeAllocator
{
    private long _next = start;
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<IdRange> AllocateAsync(int size, CancellationToken cancellationToken)
    {
        await Task.Yield();
        Interlocked.Increment(ref _calls);
        var end = Interlocked.Add(ref _next, size);
        return new IdRange(end - size, end);
    }
}
