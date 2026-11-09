using Microsoft.Extensions.Caching.Hybrid;
using UrlShortener.Core.Links;
using UrlShortener.Core.Users;

namespace UrlShortener.Api.Caching;

/// <summary>
/// Read-through cache in front of the link store. Each instance has a fast in-memory cache (L1);
/// Redis (L2) is shared by all instances when configured. <see cref="HybridCache"/> also ensures
/// that many simultaneous misses for one code make a single database call (stampede protection),
/// which matters when a link goes viral.
/// </summary>
/// <remarks>
/// Links never change and never expire, so positive entries can live a long time and never need
/// invalidating. Misses are cached briefly too (negative caching), so bots probing random codes
/// can't hammer the database.
/// </remarks>
public sealed class LinkCache(HybridCache cache, ILinkRepository repository)
{
    internal static readonly HybridCacheEntryOptions Found = new()
    {
        Expiration = TimeSpan.FromDays(7),
        LocalCacheExpiration = TimeSpan.FromMinutes(10),
    };

    internal static readonly HybridCacheEntryOptions Missing = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    public ValueTask<ShortLink?> GetAsync(string code, CancellationToken cancellationToken) =>
        CachedLookup.GetAsync(cache, Key(code), (repository, code),
            static (state, ct) => new ValueTask<ShortLink?>(state.repository.GetAsync(state.code, ct)),
            Found, Missing, cancellationToken);

    /// <summary>Removes a cached miss once the code is claimed, so a new alias works straight away.</summary>
    public ValueTask ForgetAsync(string code, CancellationToken cancellationToken) =>
        cache.RemoveAsync(Key(code), cancellationToken);

    private static string Key(string code) => $"link:{code}";
}

/// <summary>Caches users for API-key authentication, so most authenticated calls skip the user store.</summary>
public sealed class UserCache(HybridCache cache, IUserRepository repository)
{
    private static readonly HybridCacheEntryOptions Found = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(2),
    };

    private static readonly HybridCacheEntryOptions Missing = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    public ValueTask<User?> GetAsync(string id, CancellationToken cancellationToken) =>
        CachedLookup.GetAsync(cache, $"user:{id}", (repository, id),
            static (state, ct) => new ValueTask<User?>(state.repository.GetAsync(state.id, ct)),
            Found, Missing, cancellationToken);
}

internal static class CachedLookup
{
    /// <summary>
    /// <see cref="HybridCache"/> takes one set of entry options up front. To give misses a much
    /// shorter lifetime than hits, a fresh miss is rewritten with <paramref name="missing"/>. Only
    /// a miss that was just loaded is rewritten, never a miss served from the cache, so repeated
    /// 404s don't each write to Redis.
    /// </summary>
    public static async ValueTask<T?> GetAsync<TState, T>(
        HybridCache cache,
        string key,
        TState state,
        Func<TState, CancellationToken, ValueTask<T?>> load,
        HybridCacheEntryOptions found,
        HybridCacheEntryOptions missing,
        CancellationToken cancellationToken)
        where T : class
    {
        var lookup = new Lookup<TState, T>(state, load);
        var value = await cache.GetOrCreateAsync(key, lookup, static (l, ct) => l.LoadAsync(ct), found, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (value is null && lookup.Loaded)
        {
            await cache.SetAsync<T?>(key, null, missing, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return value;
    }

    private sealed class Lookup<TState, T>(TState state, Func<TState, CancellationToken, ValueTask<T?>> load)
        where T : class
    {
        public bool Loaded { get; private set; }

        public ValueTask<T?> LoadAsync(CancellationToken cancellationToken)
        {
            Loaded = true;
            return load(state, cancellationToken);
        }
    }
}
