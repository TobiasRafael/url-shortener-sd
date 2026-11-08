using System.Buffers;
using System.Collections.Frozen;

namespace UrlShortener.Core.Links;

/// <summary>Validation rules for destination URLs and short codes.</summary>
public static class LinkRules
{
    public const int MaxUrlLength = 2048;
    public const int MinAliasLength = 3;
    public const int MaxAliasLength = 16;

    private static readonly SearchValues<char> AliasChars =
        SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_");

    /// <summary>
    /// Paths that the API or the web app own. A short code can never be one of these.
    /// Matching ignores case, so <c>API</c> and <c>Health</c> are rejected too.
    /// </summary>
    public static readonly FrozenSet<string> ReservedWords = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "api", "health", "alive", "openapi", "scalar", "swagger", "docs", "admin", "login", "logout",
        "signup", "register", "account", "settings", "static", "assets", "favicon", "robots", "sitemap",
        "links", "stats", "top", "developers", "about", "terms", "privacy", "help", "status", "www");

    /// <summary>
    /// Checks the format only: 3–16 characters from <c>[A-Za-z0-9_-]</c>. Every generated
    /// 7-character base62 code passes.
    /// </summary>
    public static bool IsValidCodeFormat(string? code) =>
        code is { Length: >= MinAliasLength and <= MaxAliasLength } &&
        !code.AsSpan().ContainsAnyExcept(AliasChars);

    public static bool IsReserved(string code) => ReservedWords.Contains(code);

    public static string? ValidateAlias(string alias)
    {
        if (alias.Length is < MinAliasLength or > MaxAliasLength)
        {
            return $"Custom alias must be between {MinAliasLength} and {MaxAliasLength} characters.";
        }
        if (alias.AsSpan().ContainsAnyExcept(AliasChars))
        {
            return "Custom alias may only contain letters, digits, '-' and '_'.";
        }
        if (alias[0] is '-' or '_' || alias[^1] is '-' or '_')
        {
            return "Custom alias must start and end with a letter or digit.";
        }
        if (IsReserved(alias))
        {
            return $"'{alias}' is a reserved word.";
        }
        return null;
    }

    /// <summary>
    /// Validates and normalises a destination URL. Only absolute http(s) URLs are accepted,
    /// and URLs that point back at the shortener itself are rejected (they would redirect in a loop).
    /// </summary>
    public static bool TryNormalizeUrl(
        string? input,
        IReadOnlySet<string> blockedHosts,
        out string normalized,
        out string? error)
    {
        normalized = string.Empty;
        var candidate = input?.Trim();

        if (string.IsNullOrEmpty(candidate))
        {
            error = "A URL is required.";
            return false;
        }
        if (candidate.Length > MaxUrlLength)
        {
            error = $"URL must be at most {MaxUrlLength} characters.";
            return false;
        }
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            error = "URL must be an absolute http:// or https:// address.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "URLs with embedded credentials are not allowed.";
            return false;
        }
        if (blockedHosts.Contains(uri.IdnHost))
        {
            error = "Links that point to this shortener are not allowed.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        if (normalized.Length > MaxUrlLength)
        {
            error = $"URL must be at most {MaxUrlLength} characters.";
            return false;
        }

        error = null;
        return true;
    }
}
