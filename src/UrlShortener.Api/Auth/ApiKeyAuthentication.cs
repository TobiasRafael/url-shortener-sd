using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using UrlShortener.Api.Caching;
using UrlShortener.Core.Users;

namespace UrlShortener.Api.Auth;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

/// <summary>
/// Authenticates <c>X-Api-Key: usk_{userId}.{secret}</c>. The user ID in the key allows a direct
/// (cached) point read of the user, then the secret is checked against the stored hash in constant time.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    UserCache users) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values))
        {
            return AuthenticateResult.NoResult(); // anonymous: allowed wherever auth isn't required
        }

        if (!ApiKeys.TryParse(values.ToString(), out var userId, out var secret))
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var user = await users.GetAsync(userId, Context.RequestAborted).ConfigureAwait(false);
        if (user is null || !ApiKeys.Verify(secret, user.ApiKeyHash))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"{ApiKeyDefaults.Scheme} header=\"{ApiKeyDefaults.HeaderName}\"";
        return base.HandleChallengeAsync(properties);
    }
}

public static class ApiKeyPrincipalExtensions
{
    public static string? GetUserId(this ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true ? principal.FindFirstValue(ClaimTypes.NameIdentifier) : null;
}

/// <summary>
/// On endpoints where a key is optional (like creating a link), a key that was sent but is
/// invalid must be rejected. Silently treating the caller as anonymous would hide a
/// misconfigured integration.
/// </summary>
public sealed class RejectInvalidApiKeyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        if (http.Request.Headers.ContainsKey(ApiKeyDefaults.HeaderName) && http.User.Identity?.IsAuthenticated != true)
        {
            http.Response.Headers.WWWAuthenticate = $"{ApiKeyDefaults.Scheme} header=\"{ApiKeyDefaults.HeaderName}\"";
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Invalid API key",
                detail: $"The {ApiKeyDefaults.HeaderName} header was sent but is not a valid key. Omit it to call anonymously.");
        }
        return await next(context).ConfigureAwait(false);
    }
}
