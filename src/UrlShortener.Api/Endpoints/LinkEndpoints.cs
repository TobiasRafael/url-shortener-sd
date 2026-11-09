using Microsoft.AspNetCore.Http.HttpResults;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Auth;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Hosting;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;

namespace UrlShortener.Api.Endpoints;

public static class LinkEndpoints
{
    public const int DefaultStatsDays = 30;
    public const int MaxStatsDays = 365;

    public static IEndpointRouteBuilder MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var links = app.MapGroup("/api/v1/links")
            .WithTags("Links")
            .AddEndpointFilter<RejectInvalidApiKeyFilter>();

        links.MapPost("/", CreateAsync)
            .WithName("CreateLink")
            .WithSummary("Shorten a URL")
            .WithDescription(
                "Creates a short link, or returns the caller's existing link for the same URL (200). " +
                "Anonymous use is allowed and rate-limited per IP; send X-Api-Key to own the link and get higher limits.")
            .RequireRateLimiting(RateLimitPolicies.CreateLink);

        links.MapGet("/{code}", GetAsync)
            .WithName("GetLink")
            .WithSummary("Get a short link's details");

        links.MapGet("/{code}/stats", GetStatsAsync)
            .WithName("GetLinkStats")
            .WithSummary("Get click statistics for a short link");

        return app;
    }

    internal static async Task<Results<Created<LinkResponse>, Ok<LinkResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateLinkRequest? request,
        HttpContext http,
        LinkService service,
        LinkCache cache,
        ShortUrlFactory urls,
        ApiMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["A JSON body is required."] });
        }

        // The configured short domain is blocked in LinkService; this also covers whatever host
        // the request came in on (local dev, a preview environment, ...).
        if (Uri.TryCreate(request.LongUrl?.Trim(), UriKind.Absolute, out var target) &&
            string.Equals(target.IdnHost, http.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["longUrl"] = ["Links that point to this shortener are not allowed."],
            });
        }

        var result = await service.CreateAsync(
            new CreateLinkCommand(request.LongUrl, request.CustomAlias, http.User.GetUserId()), cancellationToken);

        switch (result.Status)
        {
            case CreateLinkStatus.Created:
                var link = result.Link!;
                metrics.LinksCreated.Add(1);
                await cache.ForgetAsync(link.Code, cancellationToken); // drop any cached "not found"
                return TypedResults.Created($"/api/v1/links/{link.Code}", ToResponse(link, urls, http.Request));

            case CreateLinkStatus.Existing:
                return TypedResults.Ok(ToResponse(result.Link!, urls, http.Request));

            case CreateLinkStatus.AliasTaken:
                return TypedResults.Problem(new HttpValidationProblemDetails(result.Errors!.ToDictionary())
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Alias already taken",
                });

            default:
                return TypedResults.ValidationProblem(result.Errors!.ToDictionary());
        }
    }

    internal static async Task<Results<Ok<LinkResponse>, ProblemHttpResult>> GetAsync(
        string code,
        HttpContext http,
        LinkCache cache,
        ShortUrlFactory urls,
        CancellationToken cancellationToken)
    {
        var link = LinkRules.IsValidCodeFormat(code) ? await cache.GetAsync(code, cancellationToken) : null;
        return link is null
            ? NotFound(code)
            : TypedResults.Ok(ToResponse(link, urls, http.Request));
    }

    internal static async Task<Results<Ok<LinkStatsResponse>, ValidationProblem, ProblemHttpResult>> GetStatsAsync(
        string code,
        int? days,
        HttpContext http,
        LinkCache cache,
        IStatsStore stats,
        ShortUrlFactory urls,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var window = days ?? DefaultStatsDays;
        if (window is < 1 or > MaxStatsDays)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["days"] = [$"days must be between 1 and {MaxStatsDays}."],
            });
        }

        var link = LinkRules.IsValidCodeFormat(code) ? await cache.GetAsync(code, cancellationToken) : null;
        if (link is null)
        {
            return NotFound(code);
        }

        var linkStats = await stats.GetAsync(link.Code, cancellationToken) ?? LinkStats.Empty(link.Code);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var daily = Enumerable.Range(0, window)
            .Select(offset => today.AddDays(offset - window + 1))
            .Select(day => new DailyClicks(day, linkStats.Daily.GetValueOrDefault(day)))
            .ToList();

        return TypedResults.Ok(new LinkStatsResponse(
            link.Code,
            urls.For(http.Request, link.Code),
            link.LongUrl,
            linkStats.TotalClicks,
            linkStats.LastClickedAt,
            daily));
    }

    internal static LinkResponse ToResponse(ShortLink link, ShortUrlFactory urls, HttpRequest request) =>
        new(link.Code, urls.For(request, link.Code), link.LongUrl, link.CreatedAt, link.IsCustom);

    private static ProblemHttpResult NotFound(string code) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Short link not found", detail: $"No short link '{code}' exists.");
}
