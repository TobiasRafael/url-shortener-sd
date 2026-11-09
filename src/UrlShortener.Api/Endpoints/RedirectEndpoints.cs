using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Options;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;

namespace UrlShortener.Api.Endpoints;

public static class RedirectEndpoints
{
    public static IEndpointRouteBuilder MapRedirectEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // GET and HEAD: link-preview bots often use HEAD. Only GET counts as a click.
        app.MapMethods("/{code}", [HttpMethods.Get, HttpMethods.Head], RedirectAsync)
            .WithName("Redirect")
            .WithTags("Redirect")
            .WithSummary("Follow a short link")
            .WithDescription(
                "Responds 302 Found with the destination in Location. 302 (not 301) is deliberate: " +
                "browsers don't cache it, so every click reaches the service and can be counted.");

        app.MapGet("/", Root).ExcludeFromDescription();

        return app;
    }

    internal static async Task<Results<RedirectHttpResult, ProblemHttpResult>> RedirectAsync(
        string code,
        HttpContext http,
        LinkCache cache,
        ClickBuffer clicks,
        ApiMetrics metrics,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // Cheap format check first: junk like "favicon.ico" never reaches the cache or the database.
        var link = LinkRules.IsValidCodeFormat(code) ? await cache.GetAsync(code, cancellationToken) : null;
        if (link is null)
        {
            metrics.RedirectMisses.Add(1);
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Short link not found");
        }

        metrics.Redirects.Add(1);
        if (HttpMethods.IsGet(http.Request.Method))
        {
            clicks.Enqueue(new ClickEvent(link.Code, timeProvider.GetUtcNow())); // never blocks
        }

        http.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.Redirect(link.LongUrl, permanent: false);
    }

    private static IResult Root(IOptions<ShortLinkOptions> options) =>
        options.Value.FrontendUrl is { Length: > 0 } frontend
            ? TypedResults.Redirect(frontend)
            : TypedResults.Ok(new { name = "URL Shortener API", docs = "/scalar", openApi = "/openapi/v1.json" });
}
