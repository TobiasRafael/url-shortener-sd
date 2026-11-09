using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace UrlShortener.AppHost.Tests;

/// <summary>
/// Starts the whole distributed app (the API, the Cosmos DB emulator and Redis in Docker) once
/// for the test class, the same way <c>aspire run</c> does locally.
/// </summary>
/// <remarks>
/// Without Docker these tests are <i>skipped</i>. CI sets <c>REQUIRE_COSMOS_EMULATOR=true</c>
/// so a broken environment fails the build instead.
/// </remarks>
public sealed class RunningAppFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    private DistributedApplication? _app;

    public HttpClient? Api { get; private set; }

    public string? UnavailableReason { get; private set; }

    private static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable("REQUIRE_COSMOS_EMULATOR"), "true", StringComparison.OrdinalIgnoreCase);

    public async ValueTask InitializeAsync()
    {
        try
        {
            if (!await DockerIsRunningAsync())
            {
                throw new InvalidOperationException("Docker is not running");
            }

            using var startup = new CancellationTokenSource(StartupTimeout);
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.UrlShortener_AppHost>(
                ["AppHost:PersistentContainers=false"], startup.Token);

            // Keep test output readable: resource logs are in the Aspire dashboard's domain, and the
            // emulator's readiness probe fails by design while it boots.
            builder.Services.AddLogging(logging => logging
                .SetMinimumLevel(LogLevel.Warning)
                .AddFilter("Microsoft.Extensions.Diagnostics.HealthChecks", LogLevel.None));

            _app = await builder.BuildAsync(startup.Token);
            await _app.StartAsync(startup.Token);
            await _app.ResourceNotifications.WaitForResourceHealthyAsync("api", startup.Token);

            // Not app.CreateHttpClient: the tests assert on the 302 itself, so redirects must not be followed.
#pragma warning disable CA2000 // the client owns and disposes the handler
            Api = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = _app.GetEndpoint("api", "http"),
            };
#pragma warning restore CA2000
        }
        catch (Exception ex) when (!IsRequired)
        {
            UnavailableReason = $"Distributed app unavailable ({ex.GetType().Name}: {ex.Message})";
        }
    }

    public HttpClient RequireApi()
    {
        Assert.SkipWhen(Api is null, UnavailableReason ?? "Distributed app unavailable");
        return Api!;
    }

    public async ValueTask DisposeAsync()
    {
        Api?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static async Task<bool> DockerIsRunningAsync()
    {
        try
        {
            using var docker = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (docker is null)
            {
                return false;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await docker.WaitForExitAsync(timeout.Token);
            return docker.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed class RunningAppTests(RunningAppFixture fixture) : IClassFixture<RunningAppFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_link_created_through_the_api_redirects_and_counts_clicks_against_real_cosmos()
    {
        var api = fixture.RequireApi();

        using var registration = await api.PostAsJsonAsync(
            "/api/v1/users", new { name = "AppHost Test", email = $"{Guid.NewGuid():N}@example.com" }, Ct);
        registration.StatusCode.ShouldBe(HttpStatusCode.Created);
        var apiKey = (await registration.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("apiKey").GetString()!;

        var longUrl = $"https://example.com/apphost/{Guid.NewGuid():N}";
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/links") { Content = JsonContent.Create(new { longUrl }) };
        create.Headers.Add("X-Api-Key", apiKey);
        using var created = await api.SendAsync(create, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("code").GetString()!;
        code.Length.ShouldBe(7);

        using (var redirect = await api.GetAsync($"/{code}", Ct))
        {
            redirect.StatusCode.ShouldBe(HttpStatusCode.Found);
            redirect.Headers.Location.ShouldBe(new Uri(longUrl));
        }

        // Clicks are buffered and flushed to Cosmos in the background, so poll briefly.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long clicks;
        do
        {
            await Task.Delay(250, Ct);
            var stats = await api.GetFromJsonAsync<JsonElement>($"/api/v1/links/{code}/stats", Json, Ct);
            clicks = stats.GetProperty("totalClicks").GetInt64();
        }
        while (clicks < 1 && DateTime.UtcNow < deadline);
        clicks.ShouldBe(1);

        // The owner's links come from a cross-partition Cosmos query.
        using var mine = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me/links");
        mine.Headers.Add("X-Api-Key", apiKey);
        using var mineResponse = await api.SendAsync(mine, Ct);
        var links = await mineResponse.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        links.EnumerateArray().Select(l => l.GetProperty("code").GetString()).ShouldBe([code]);
    }

    [Fact]
    public async Task The_api_reports_healthy_with_its_dependencies_connected()
    {
        var api = fixture.RequireApi();

        using var response = await api.GetAsync("/health", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
