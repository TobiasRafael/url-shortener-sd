using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using UrlShortener.Api.Auth;
using UrlShortener.Api.Contracts;

namespace UrlShortener.Api.Tests.Infrastructure;

/// <summary>The real API, in memory: in-memory storage, the direct click pipeline, a fast flush interval.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string PublicBaseUrl = "https://sho.rt";
    public const string FrontendUrl = "https://app.sho.rt";

    protected virtual IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Logging:LogLevel:Default"] = "Warning",
        ["Storage:Provider"] = "InMemory",
        ["Analytics:Pipeline"] = "Direct",
        ["ShortCodes:Secret"] = "api-tests-secret-of-sufficient-length",
        ["ShortLinks:PublicBaseUrl"] = PublicBaseUrl,
        ["ShortLinks:FrontendUrl"] = FrontendUrl,
        ["ShortLinks:AllowedOrigins:0"] = FrontendUrl,
        ["RateLimits:AnonymousCreatesPerMinute"] = "10000",
        ["RateLimits:ApiKeyCreatesPerMinute"] = "10000",
        ["RateLimits:RegistrationsPerHour"] = "10000",
        ["ClickBatching:FlushInterval"] = "00:00:00.050",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>A client that doesn't follow redirects, so tests can assert on the 302 itself.</summary>
    public HttpClient CreateNonRedirectingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

/// <summary>Same API, with tight limits for the rate-limiting tests.</summary>
public sealed class RateLimitedApiFactory : ApiFactory
{
    public const int AnonymousLimit = 3;

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["RateLimits:AnonymousCreatesPerMinute"] = AnonymousLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["RateLimits:ApiKeyCreatesPerMinute"] = "100",
    };
}

internal static class ApiClientExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<HttpResponseMessage> CreateLinkAsync(this HttpClient client, string longUrl, string? alias = null, string? apiKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/links")
        {
            Content = JsonContent.Create(new CreateLinkRequest(longUrl, alias)),
        };
        if (apiKey is not null)
        {
            request.Headers.Add(ApiKeyDefaults.HeaderName, apiKey);
        }
        return await client.SendAsync(request, Ct); // awaited inside the using: the request must outlive the send
    }

    public static async Task<LinkResponse> CreateLinkOkAsync(this HttpClient client, string longUrl, string? alias = null, string? apiKey = null)
    {
        using var response = await client.CreateLinkAsync(longUrl, alias, apiKey);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<LinkResponse>(Json, Ct))!;
    }

    public static async Task<RegisterUserResponse> RegisterAsync(this HttpClient client, string? email = null)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/users", new RegisterUserRequest("Test User", email ?? $"{Guid.NewGuid():N}@example.com"), Ct);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RegisterUserResponse>(Json, Ct))!;
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

    public static string UniqueUrl(string tag = "page") => $"https://example.com/{tag}/{Guid.NewGuid():N}";

    /// <summary>Clicks are flushed asynchronously, so poll until the assertion holds or time runs out.</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string because, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new ShouldAssertException($"Timed out waiting until {because}.");
            }
            await Task.Delay(25, Ct);
        }
    }
}
