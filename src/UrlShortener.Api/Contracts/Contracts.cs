using System.ComponentModel;

namespace UrlShortener.Api.Contracts;

// Public REST contracts. They are deliberately separate from the domain records, so the wire
// format can change independently of the model.

public sealed record CreateLinkRequest(
    [property: Description("The destination: an absolute http(s) URL, up to 2,048 characters.")] string? LongUrl,
    [property: Description("Optional custom alias: 3–16 characters of letters, digits, '-' and '_'.")] string? CustomAlias = null);

public sealed record LinkResponse(
    [property: Description("The short code, e.g. 'aZ3kP9q'.")] string Code,
    [property: Description("The full short URL to share.")] string ShortUrl,
    string LongUrl,
    DateTimeOffset CreatedAt,
    [property: Description("True if the code was chosen by the caller.")] bool IsCustom);

public sealed record DailyClicks(DateOnly Date, long Clicks);

public sealed record LinkStatsResponse(
    string Code,
    string ShortUrl,
    string LongUrl,
    long TotalClicks,
    DateTimeOffset? LastClickedAt,
    [property: Description("One entry per UTC day, oldest first, zero-filled.")] IReadOnlyList<DailyClicks> Daily);

public sealed record TopLinkResponse(
    string Code,
    string ShortUrl,
    string? LongUrl,
    long TotalClicks,
    DateTimeOffset? LastClickedAt);

public sealed record RegisterUserRequest(string? Name, string? Email);

public sealed record RegisterUserResponse(
    string Id,
    string Name,
    string Email,
    DateTimeOffset CreatedAt,
    [property: Description("Send as the X-Api-Key header. Shown only once; it can't be recovered.")] string ApiKey);

public sealed record UserResponse(string Id, string Name, string Email, DateTimeOffset CreatedAt);
