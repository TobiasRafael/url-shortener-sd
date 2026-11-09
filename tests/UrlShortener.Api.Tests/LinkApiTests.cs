using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Contracts;
using UrlShortener.Api.Tests.Infrastructure;
using static UrlShortener.Api.Tests.Infrastructure.ApiClientExtensions;

namespace UrlShortener.Api.Tests;

public sealed class LinkApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateNonRedirectingClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_returns_201_with_a_7_char_code_on_the_public_short_domain()
    {
        var url = UniqueUrl();

        using var response = await _client.CreateLinkAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var link = (await response.Content.ReadFromJsonAsync<LinkResponse>(Json, Ct))!;
        link.Code.Length.ShouldBe(7);
        link.ShortUrl.ShouldBe($"{ApiFactory.PublicBaseUrl}/{link.Code}");
        link.LongUrl.ShouldBe(url);
        link.IsCustom.ShouldBeFalse();
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/links/{link.Code}");
    }

    [Fact]
    public async Task Shortening_the_same_url_again_returns_200_with_the_same_code()
    {
        var url = UniqueUrl();
        var first = await _client.CreateLinkOkAsync(url);

        using var again = await _client.CreateLinkAsync(url);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<LinkResponse>(Json, Ct))!.Code.ShouldBe(first.Code);
    }

    [Fact]
    public async Task Custom_alias_is_created_once_then_conflicts()
    {
        var alias = $"a{Guid.NewGuid():N}"[..12];

        using var created = await _client.CreateLinkAsync(UniqueUrl(), alias);
        using var conflict = await _client.CreateLinkAsync(UniqueUrl(), alias);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<LinkResponse>(Json, Ct))!.ShouldSatisfyAllConditions(
            l => l.Code.ShouldBe(alias),
            l => l.IsCustom.ShouldBeTrue());

        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        conflict.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await conflict.ReadJsonAsync()).GetProperty("errors").TryGetProperty("customAlias", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("not a url", null, "longUrl")]
    [InlineData("javascript:alert(1)", null, "longUrl")]
    [InlineData("https://sho.rt/abc1234", null, "longUrl")]      // the public short domain
    [InlineData("http://localhost/abc1234", null, "longUrl")]    // the host the request came in on
    [InlineData("https://example.com/", "api", "customAlias")]   // reserved word
    [InlineData("https://example.com/", "no", "customAlias")]    // too short
    [InlineData("https://example.com/", "has space", "customAlias")]
    public async Task Invalid_input_is_a_400_problem_naming_the_field(string url, string? alias, string field)
    {
        using var response = await _client.CreateLinkAsync(url, alias);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_body_is_a_400()
    {
        using var response = await _client.PostAsync("/api/v1/links", new StringContent("null", System.Text.Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Link_details_are_readable_and_unknown_codes_are_404()
    {
        var link = await _client.CreateLinkOkAsync(UniqueUrl());

        var details = await _client.GetFromJsonAsync<LinkResponse>($"/api/v1/links/{link.Code}", Json, Ct);
        using var missing = await _client.GetAsync("/api/v1/links/zzzzzz9", Ct);

        details.ShouldBe(link);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Links_created_with_an_api_key_belong_to_that_user()
    {
        var user = await _client.RegisterAsync();
        var mine = await _client.CreateLinkOkAsync(UniqueUrl("mine"), apiKey: user.ApiKey);
        await _client.CreateLinkOkAsync(UniqueUrl("anonymous"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me/links");
        request.Headers.Add("X-Api-Key", user.ApiKey);
        using var response = await _client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var links = (await response.Content.ReadFromJsonAsync<List<LinkResponse>>(Json, Ct))!;
        links.ShouldHaveSingleItem().Code.ShouldBe(mine.Code);
    }

    [Fact]
    public async Task Dedup_is_per_owner_so_users_never_share_links()
    {
        var url = UniqueUrl();
        var alice = await _client.RegisterAsync();
        var bob = await _client.RegisterAsync();

        var aliceLink = await _client.CreateLinkOkAsync(url, apiKey: alice.ApiKey);
        var bobLink = await _client.CreateLinkOkAsync(url, apiKey: bob.ApiKey);
        var anonymousLink = await _client.CreateLinkOkAsync(url);

        new[] { aliceLink.Code, bobLink.Code, anonymousLink.Code }.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task An_invalid_api_key_is_rejected_rather_than_treated_as_anonymous()
    {
        using var response = await _client.CreateLinkAsync(UniqueUrl(), apiKey: "usk_0192f1a2b3c47d8e9f00112233445566.wrong");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("X-Api-Key");
    }
}
