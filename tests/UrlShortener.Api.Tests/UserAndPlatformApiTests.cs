using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Tests.Infrastructure;
using static UrlShortener.Api.Tests.Infrastructure.ApiClientExtensions;

namespace UrlShortener.Api.Tests;

public sealed class UserApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateNonRedirectingClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registering_returns_a_key_that_authenticates_as_that_user()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        var registered = await _client.RegisterAsync(email);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("X-Api-Key", registered.ApiKey);
        using var response = await _client.SendAsync(request, Ct);

        registered.ApiKey.ShouldStartWith("usk_");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = (await response.Content.ReadFromJsonAsync<UserResponse>(Json, Ct))!;
        me.ShouldBe(new UserResponse(registered.Id, "Test User", email, registered.CreatedAt));
    }

    [Fact]
    public async Task A_second_registration_with_the_same_email_conflicts()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await _client.RegisterAsync(email);

        using var response = await _client.PostAsJsonAsync("/api/v1/users", new RegisterUserRequest("Other", email.ToUpperInvariant()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("", "a@example.com")]
    [InlineData("Ada", "not-an-email")]
    public async Task Invalid_registrations_are_400(string name, string email)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/users", new RegisterUserRequest(name, email), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Me_requires_an_api_key_and_says_how_to_send_one()
    {
        using var response = await _client.GetAsync("/api/v1/users/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("ApiKey header=\"X-Api-Key\"");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("usk_0192f1a2b3c47d8e9f00112233445566.forged-secret")]
    public async Task Me_rejects_malformed_and_forged_keys(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_user_id_with_the_wrong_secret_is_rejected()
    {
        var registered = await _client.RegisterAsync();
        var forged = registered.ApiKey[..(registered.ApiKey.IndexOf('.', StringComparison.Ordinal) + 1)] + "not-the-secret";

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("X-Api-Key", forged);
        using var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

public sealed class RateLimitApiTests(RateLimitedApiFactory factory) : IClassFixture<RateLimitedApiFactory>
{
    private readonly HttpClient _client = factory.CreateNonRedirectingClient();

    [Fact]
    public async Task Anonymous_callers_are_limited_and_told_when_to_retry_while_key_holders_have_their_own_quota()
    {
        // Registering first: it uses its own policy, so the anonymous link quota stays untouched.
        var user = await _client.RegisterAsync();

        for (var i = 0; i < RateLimitedApiFactory.AnonymousLimit; i++)
        {
            using var allowed = await _client.CreateLinkAsync(UniqueUrl());
            allowed.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var limited = await _client.CreateLinkAsync(UniqueUrl());
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        limited.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        using var withKey = await _client.CreateLinkAsync(UniqueUrl(), apiKey: user.ApiKey);
        withKey.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Redirects_are_never_rate_limited()
    {
        var user = await _client.RegisterAsync();
        var link = await _client.CreateLinkOkAsync(UniqueUrl(), apiKey: user.ApiKey);

        for (var i = 0; i < 50; i++)
        {
            using var response = await _client.GetAsync($"/{link.Code}", TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Found);
        }
    }
}

public sealed class PlatformApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateNonRedirectingClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenApi_document_describes_every_endpoint_and_the_api_key_scheme()
    {
        using var response = await _client.GetAsync("/openapi/v1.json", Ct);
        var document = await response.ReadJsonAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        document.GetProperty("info").GetProperty("title").GetString().ShouldBe("URL Shortener API");
        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.ShouldBe(
            ["/api/v1/links", "/api/v1/links/{code}", "/api/v1/links/{code}/stats", "/api/v1/users", "/api/v1/users/me",
             "/api/v1/users/me/links", "/api/v1/analytics/top", "/{code}"],
            ignoreOrder: true);
        document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey")
            .GetProperty("name").GetString().ShouldBe("X-Api-Key");
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        using var response = await _client.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Api_docs_ui_is_served()
    {
        using var response = await factory.CreateClient().GetAsync("/scalar/", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
    }

    [Theory]
    [InlineData(ApiFactory.FrontendUrl, true)]
    [InlineData("https://evil.example", false)]
    public async Task Cors_allows_only_the_configured_web_app(string origin, bool allowed)
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/links");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type,x-api-key");

        using var response = await _client.SendAsync(preflight, Ct);

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values).ShouldBe(allowed);
        if (allowed)
        {
            values!.ShouldHaveSingleItem().ShouldBe(origin);
        }
    }
}
