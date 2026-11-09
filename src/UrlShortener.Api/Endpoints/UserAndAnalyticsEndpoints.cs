using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using UrlShortener.Api.Auth;
using UrlShortener.Api.Caching;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Hosting;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;
using UrlShortener.Core.Users;

namespace UrlShortener.Api.Endpoints;

public static class UserEndpoints
{
    public const int MaxLinksPage = 100;

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var users = app.MapGroup("/api/v1/users").WithTags("Users");

        users.MapPost("/", RegisterAsync)
            .WithName("RegisterUser")
            .WithSummary("Register and receive an API key")
            .WithDescription("The API key is returned once and only once. Store it securely.")
            .RequireRateLimiting(RateLimitPolicies.Register);

        var me = users.MapGroup("/me").RequireAuthorization();
        me.MapGet("/", GetMeAsync).WithName("GetCurrentUser").WithSummary("Get the API key's owner");
        me.MapGet("/links", GetMyLinksAsync).WithName("GetCurrentUserLinks").WithSummary("List the caller's links, newest first");

        return app;
    }

    internal static async Task<Results<Created<RegisterUserResponse>, ValidationProblem, ProblemHttpResult>> RegisterAsync(
        RegisterUserRequest? request,
        UserService service,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["A JSON body is required."] });
        }

        var result = await service.RegisterAsync(request.Name, request.Email, cancellationToken);
        return result.Status switch
        {
            RegisterUserStatus.Created => TypedResults.Created(
                "/api/v1/users/me",
                new RegisterUserResponse(result.User!.Id, result.User.Name, result.User.Email, result.User.CreatedAt, result.ApiKey!)),

            RegisterUserStatus.EmailTaken => TypedResults.Problem(new HttpValidationProblemDetails(result.Errors!.ToDictionary())
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Email already registered",
            }),

            _ => TypedResults.ValidationProblem(result.Errors!.ToDictionary()),
        };
    }

    internal static async Task<Results<Ok<UserResponse>, UnauthorizedHttpResult>> GetMeAsync(
        HttpContext http,
        UserCache users,
        CancellationToken cancellationToken)
    {
        var user = http.User.GetUserId() is { } id ? await users.GetAsync(id, cancellationToken) : null;
        return user is null
            ? TypedResults.Unauthorized()
            : TypedResults.Ok(new UserResponse(user.Id, user.Name, user.Email, user.CreatedAt));
    }

    internal static async Task<Results<Ok<List<LinkResponse>>, ValidationProblem, UnauthorizedHttpResult>> GetMyLinksAsync(
        int? limit,
        HttpContext http,
        ILinkRepository links,
        ShortUrlFactory urls,
        CancellationToken cancellationToken)
    {
        var take = limit ?? 20;
        if (take is < 1 or > MaxLinksPage)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = [$"limit must be between 1 and {MaxLinksPage}."] });
        }
        if (http.User.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var owned = await links.GetByUserAsync(userId, take, cancellationToken);
        return TypedResults.Ok(owned.Select(l => LinkEndpoints.ToResponse(l, urls, http.Request)).ToList());
    }
}

public static class AnalyticsEndpoints
{
    public const int MaxTop = 50;

    private static readonly HybridCacheEntryOptions TopCacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/api/v1/analytics")
            .WithTags("Analytics")
            .MapGet("/top", GetTopAsync)
            .WithName("GetTopLinks")
            .WithSummary("Most-clicked links")
            .WithDescription("Cached for 30 seconds, so the ranking may lag live traffic slightly.");

        return app;
    }

    internal static async Task<Results<Ok<List<TopLinkResponse>>, ValidationProblem>> GetTopAsync(
        int? limit,
        HttpContext http,
        IStatsStore stats,
        HybridCache cache,
        LinkCache links,
        ShortUrlFactory urls,
        CancellationToken cancellationToken)
    {
        var take = limit ?? 10;
        if (take is < 1 or > MaxTop)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = [$"limit must be between 1 and {MaxTop}."] });
        }

        var top = await cache.GetOrCreateAsync(
            $"top:{take}",
            (stats, take),
            static async (state, ct) => (await state.stats.GetTopAsync(state.take, ct)).ToArray(),
            TopCacheOptions,
            cancellationToken: cancellationToken);

        var destinations = await Task.WhenAll(top.Select(t => links.GetAsync(t.Code, cancellationToken).AsTask()));

        return TypedResults.Ok(top
            .Select((t, i) => new TopLinkResponse(t.Code, urls.For(http.Request, t.Code), destinations[i]?.LongUrl, t.TotalClicks, t.LastClickedAt))
            .ToList());
    }
}
