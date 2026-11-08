namespace UrlShortener.Core.Links;

/// <summary>A short code mapped to its destination. Links are immutable and never expire.</summary>
public sealed record ShortLink(
    string Code,
    string LongUrl,
    DateTimeOffset CreatedAt,
    string? UserId,
    bool IsCustom);

public interface ILinkRepository
{
    /// <summary>Inserts the link. Returns <c>false</c> if the code is already taken; never overwrites.</summary>
    Task<bool> TryCreateAsync(ShortLink link, CancellationToken cancellationToken);

    Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<ShortLink>> GetByUserAsync(string userId, int limit, CancellationToken cancellationToken);
}
