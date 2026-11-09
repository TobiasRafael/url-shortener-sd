using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Tests.Infrastructure;
using static UrlShortener.Api.Tests.Infrastructure.ApiClientExtensions;

namespace UrlShortener.Api.Tests;

public sealed class RedirectAndAnalyticsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateNonRedirectingClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<LinkStatsResponse> StatsAsync(string code, int? days = null) =>
        (await _client.GetFromJsonAsync<LinkStatsResponse>($"/api/v1/links/{code}/stats{(days is null ? "" : $"?days={days}")}", Json, Ct))!;

    // ---- Redirect ------------------------------------------------------------------------

    [Fact]
    public async Task Following_a_short_link_is_an_uncacheable_302_to_the_destination()
    {
        var link = await _client.CreateLinkOkAsync(UniqueUrl());

        using var response = await _client.GetAsync($"/{link.Code}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldBe(new Uri(link.LongUrl));
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/zzzzzz9")]      // well-formed but unknown
    [InlineData("/favicon.ico")]  // malformed: rejected before any lookup
    public async Task Unknown_or_malformed_codes_are_404_problems(string path)
    {
        using var response = await _client.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task A_new_alias_works_immediately_even_after_a_cached_miss()
    {
        var alias = $"n{Guid.NewGuid():N}"[..10];
        using (var before = await _client.GetAsync($"/{alias}", Ct))
        {
            before.StatusCode.ShouldBe(HttpStatusCode.NotFound); // the miss is now cached
        }

        await _client.CreateLinkOkAsync(UniqueUrl(), alias);

        using var after = await _client.GetAsync($"/{alias}", Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.Found);
    }

    [Fact]
    public async Task The_short_domain_root_sends_visitors_to_the_web_app()
    {
        using var response = await _client.GetAsync("/", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldBe(new Uri(ApiFactory.FrontendUrl));
    }

    // ---- Click analytics -----------------------------------------------------------------

    [Fact]
    public async Task Clicks_are_counted_in_the_links_stats()
    {
        var link = await _client.CreateLinkOkAsync(UniqueUrl());

        for (var i = 0; i < 3; i++)
        {
            using var _ = await _client.GetAsync($"/{link.Code}", Ct);
        }

        await EventuallyAsync(async () => (await StatsAsync(link.Code)).TotalClicks == 3, "3 clicks are counted");
        var stats = await StatsAsync(link.Code);
        stats.LastClickedAt.ShouldNotBeNull();
        stats.Daily[^1].Clicks.ShouldBe(3);
        stats.LongUrl.ShouldBe(link.LongUrl);
    }

    [Fact]
    public async Task Head_requests_redirect_but_are_not_counted_as_clicks()
    {
        var link = await _client.CreateLinkOkAsync(UniqueUrl());

        using (var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/{link.Code}"), Ct))
        {
            head.StatusCode.ShouldBe(HttpStatusCode.Found);
        }
        using (var get = await _client.GetAsync($"/{link.Code}", Ct))
        {
            get.StatusCode.ShouldBe(HttpStatusCode.Found);
        }

        await EventuallyAsync(async () => (await StatsAsync(link.Code)).TotalClicks >= 1, "the GET is counted");
        await Task.Delay(200, Ct); // give a stray HEAD click time to show up, if it were counted
        (await StatsAsync(link.Code)).TotalClicks.ShouldBe(1);
    }

    [Fact]
    public async Task Stats_cover_the_requested_window_zero_filled_and_oldest_first()
    {
        var link = await _client.CreateLinkOkAsync(UniqueUrl());

        var stats = await StatsAsync(link.Code, days: 7);

        stats.TotalClicks.ShouldBe(0);
        stats.LastClickedAt.ShouldBeNull();
        stats.Daily.Count.ShouldBe(7);
        stats.Daily.ShouldAllBe(d => d.Clicks == 0);
        stats.Daily.Select(d => d.Date).ShouldBeInOrder(SortDirection.Ascending);
        stats.Daily[^1].Date.ShouldBe(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task Stats_reject_windows_outside_1_to_365_days(int days)
    {
        var link = await _client.CreateLinkOkAsync(UniqueUrl());

        using var response = await _client.GetAsync($"/api/v1/links/{link.Code}/stats?days={days}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stats_for_an_unknown_link_are_404()
    {
        using var response = await _client.GetAsync("/api/v1/links/zzzzzz8/stats", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Top_links_rank_by_clicks_and_include_destinations()
    {
        var hot = await _client.CreateLinkOkAsync(UniqueUrl("hot"));
        for (var i = 0; i < 25; i++)
        {
            using var _ = await _client.GetAsync($"/{hot.Code}", Ct);
        }
        await EventuallyAsync(async () => (await StatsAsync(hot.Code)).TotalClicks == 25, "25 clicks are counted");

        // The ranking is cached for 30s; use a limit no other test asks for, so the first read is fresh.
        var top = (await _client.GetFromJsonAsync<List<TopLinkResponse>>("/api/v1/analytics/top?limit=49", Json, Ct))!;

        top.ShouldNotBeEmpty();
        top.Select(t => t.TotalClicks).ShouldBeInOrder(SortDirection.Descending);
        var entry = top.SingleOrDefault(t => t.Code == hot.Code).ShouldNotBeNull();
        entry.LongUrl.ShouldBe(hot.LongUrl);
        entry.ShortUrl.ShouldBe(hot.ShortUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task Top_rejects_limits_outside_1_to_50(int limit)
    {
        using var response = await _client.GetAsync($"/api/v1/analytics/top?limit={limit}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
