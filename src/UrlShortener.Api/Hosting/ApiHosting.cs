using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Auth;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Options;
using UrlShortener.Core.Links;

namespace UrlShortener.Api.Hosting;

public static class RateLimitPolicies
{
    public const string CreateLink = "create-link";
    public const string Register = "register";
}

/// <summary>Builds public short URLs from the configured base URL, or from the request's origin.</summary>
public sealed class ShortUrlFactory(IOptions<ShortLinkOptions> options)
{
    public string For(HttpRequest request, string code)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baseUrl = options.Value.PublicBaseUrl is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}";
        return $"{baseUrl}/{code}";
    }
}

public static class ApiHostingExtensions
{
    public static WebApplicationBuilder AddUrlShortenerApi(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var configuration = builder.Configuration;

        // ---- Options -----------------------------------------------------------------------
        services.AddOptions<ShortLinkOptions>().Bind(configuration.GetSection(ShortLinkOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ClickBatchingOptions>().Bind(configuration.GetSection(ClickBatchingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        var shortLinks = configuration.GetSection(ShortLinkOptions.SectionName).Get<ShortLinkOptions>() ?? new ShortLinkOptions();

        // The short domain and any extra configured hosts may never be shortened (redirect loops).
        var blocked = new HashSet<string>(shortLinks.BlockedHosts, StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(shortLinks.PublicBaseUrl, UriKind.Absolute, out var publicBase))
        {
            blocked.Add(publicBase.IdnHost);
        }
        services.AddSingleton(new LinkServiceSettings { BlockedHosts = blocked });

        // ---- Domain, storage, click pipeline ------------------------------------------------
        builder.AddUrlShortenerCore();
        builder.AddClickEventSink();

        // ---- Caching: L1 in-process, plus Redis as L2 when a "cache" connection exists ---------
        services.AddHybridCache(options => options.MaximumPayloadBytes = 64 * 1024);
        if (!string.IsNullOrEmpty(configuration.GetConnectionString("cache")))
        {
            builder.AddRedisDistributedCache("cache");
        }
        services.AddSingleton<LinkCache>();
        services.AddSingleton<UserCache>();
        services.AddSingleton<ShortUrlFactory>();

        // ---- Analytics --------------------------------------------------------------------
        services.AddSingleton<ApiMetrics>();
        services.AddSingleton<ClickBuffer>();
        services.AddHostedService<ClickFlushService>();

        // ---- Auth: optional API keys -------------------------------------------------------
        services.AddAuthentication(ApiKeyDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);
        services.AddAuthorization();

        // ---- Rate limiting: per API key when present, otherwise per client IP --------------
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRateLimitProblemAsync;

            options.AddPolicy(RateLimitPolicies.CreateLink, http =>
            {
                var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                return http.User.GetUserId() is { } userId
                    ? FixedWindow($"key:{userId}", limits.ApiKeyCreatesPerMinute, TimeSpan.FromMinutes(1))
                    : FixedWindow($"ip:{ClientIp(http)}", limits.AnonymousCreatesPerMinute, TimeSpan.FromMinutes(1));
            });

            options.AddPolicy(RateLimitPolicies.Register, http =>
            {
                var limits = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                return FixedWindow($"ip:{ClientIp(http)}", limits.RegistrationsPerHour, TimeSpan.FromHours(1));
            });
        });

        // ---- Behind a reverse proxy (Container Apps ingress / Front Door) ---------------------
        // Only trust X-Forwarded-* when told to, or clients could spoof their IP past the rate limiter.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = configuration.GetValue("ForwardedHeaders:ForwardLimit", 1);
            options.KnownIPNetworks.Clear(); // the ingress address isn't fixed in ACA
            options.KnownProxies.Clear();
        });

        // ---- CORS for the web app ------------------------------------------------------------
        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(shortLinks.AllowedOrigins)
            .WithMethods(HttpMethods.Get, HttpMethods.Post)
            .WithHeaders("Content-Type", ApiKeyDefaults.HeaderName)
            .WithExposedHeaders("Location", "Retry-After")));

        // ---- Errors and API docs ------------------------------------------------------------
        services.AddProblemDetails();
        services.AddOpenApi(options => options.AddDocumentTransformer<ApiDocumentTransformer>());

        return builder;
    }

    public static WebApplication UseUrlShortenerApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Configuration.GetValue("ForwardedHeaders:Enabled", false))
        {
            app.UseForwardedHeaders();
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        return app;
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static async ValueTask WriteRateLimitProblemAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = "Rate limit exceeded. Retry after the number of seconds in the Retry-After header, or use an API key for higher limits.",
            },
        }).ConfigureAwait(false);
    }
}

/// <summary>Document metadata, plus the API-key security scheme so the docs UI can send X-Api-Key.</summary>
internal sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "URL Shortener API",
            Version = "v1",
            Description =
                "Create short links, follow them (302 redirect), and read click analytics. " +
                "Anonymous use is rate-limited per IP; register for an API key to own links and get higher limits.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyDefaults.Scheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyDefaults.HeaderName,
            Description = "Your API key, from POST /api/v1/users. Optional on most endpoints; required on /api/v1/users/me.",
        };
        return Task.CompletedTask;
    }
}
