using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Core.Links;

/// <summary>
/// Maps <c>(owner, long URL)</c> to the short code already issued for it, so shortening the
/// same URL twice returns the existing link (the "check if longURL is in the database" step of
/// the classic design).
/// </summary>
/// <remarks>
/// Deduplication is scoped per owner. Each registered user has their own scope, so two users
/// never share a link or its click statistics. All anonymous links share one scope, which is
/// safe because anonymous links have no owner whose attribution or stats could be mixed up.
/// </remarks>
public interface IDestinationIndex
{
    Task<string?> FindCodeAsync(string destinationKey, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="code"/> for the key unless one is already recorded.
    /// Returns the code that ends up stored: <paramref name="code"/>, or the code of a
    /// concurrent request that got there first.
    /// </summary>
    Task<string> AddIfAbsentAsync(string destinationKey, string code, CancellationToken cancellationToken);
}

public static class DestinationKey
{
    private const string AnonymousScope = "anon";

    /// <summary>
    /// A fixed-length (64 hex characters) key, so URLs of up to 2 KB fit in any store's
    /// primary key. It also avoids putting raw URLs in key fields.
    /// </summary>
    public static string For(string? userId, string normalizedLongUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedLongUrl);

        var scope = string.IsNullOrEmpty(userId) ? AnonymousScope : $"user:{userId}";
        var bytes = Encoding.UTF8.GetBytes($"{scope}\n{normalizedLongUrl}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
