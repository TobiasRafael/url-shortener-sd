using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Api.Options;

public sealed class ShortLinkOptions
{
    public const string SectionName = "ShortLinks";

    /// <summary>
    /// The public origin short links are issued under, e.g. <c>https://sho.rt</c>. If unset, the
    /// request's own origin is used (fine locally; set it explicitly behind a proxy or CDN).
    /// </summary>
    [Url]
    public string? PublicBaseUrl { get; set; }

    /// <summary>The web app. A visit to the short domain's root is sent here.</summary>
    [Url]
    public string? FrontendUrl { get; set; }

    /// <summary>Extra hosts that may not be shortened (the short domain itself is always blocked).</summary>
    public string[] BlockedHosts { get; set; } = [];

    /// <summary>Browser origins allowed to call the API (the web app).</summary>
    public string[] AllowedOrigins { get; set; } = ["http://localhost:4200"];
}

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Links an anonymous caller (per client IP) may create per minute.</summary>
    [Range(1, 100_000)]
    public int AnonymousCreatesPerMinute { get; set; } = 10;

    /// <summary>Links an API-key holder may create per minute.</summary>
    [Range(1, 100_000)]
    public int ApiKeyCreatesPerMinute { get; set; } = 120;

    /// <summary>Sign-ups per client IP per hour.</summary>
    [Range(1, 100_000)]
    public int RegistrationsPerHour { get; set; } = 5;
}

public sealed class ClickBatchingOptions
{
    public const string SectionName = "ClickBatching";

    /// <summary>Clicks buffered in memory. When full, new clicks are dropped and counted; redirects never wait.</summary>
    [Range(1, 10_000_000)]
    public int Capacity { get; set; } = 100_000;

    /// <summary>How often buffered clicks are flushed to the sink.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Largest batch handed to the sink in one call.</summary>
    [Range(1, 1_000_000)]
    public int MaxBatchSize { get; set; } = 10_000;
}
