using UrlShortener.Core.Links;

namespace UrlShortener.Core.Tests.Links;

public sealed class LinkRulesTests
{
    private static readonly HashSet<string> BlockedHosts = new(["sho.rt", "localhost"], StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("abc")]
    [InlineData("promo-2026")]
    [InlineData("My_Link")]
    [InlineData("a1b2c3d4e5f6g7h8")] // exactly 16
    public void ValidateAlias_accepts_well_formed_aliases(string alias) =>
        LinkRules.ValidateAlias(alias).ShouldBeNull();

    [Theory]
    [InlineData("ab", "between 3 and 16")]
    [InlineData("a1b2c3d4e5f6g7h8i", "between 3 and 16")] // 17
    [InlineData("has space", "only contain")]
    [InlineData("dot.ted", "only contain")]
    [InlineData("emoji😀x", "only contain")]
    [InlineData("-leading", "start and end")]
    [InlineData("trailing_", "start and end")]
    [InlineData("api", "reserved")]
    [InlineData("HEALTH", "reserved")]
    [InlineData("Developers", "reserved")]
    public void ValidateAlias_rejects_bad_aliases_with_a_reason(string alias, string reason) =>
        LinkRules.ValidateAlias(alias).ShouldNotBeNull().ShouldContain(reason, Case.Insensitive);

    [Theory]
    [InlineData("abc1234", true)]
    [InlineData("my-alias_1", true)]
    [InlineData("ab", false)]
    [InlineData("a1b2c3d4e5f6g7h8i", false)]
    [InlineData("bad/code", false)]
    [InlineData("favicon.ico", false)]
    [InlineData(null, false)]
    public void IsValidCodeFormat_screens_lookups_before_they_reach_storage(string? code, bool expected) =>
        LinkRules.IsValidCodeFormat(code).ShouldBe(expected);

    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("  http://Example.COM/Path?q=1  ", "http://example.com/Path?q=1")]
    [InlineData("https://example.com:8443/a#frag", "https://example.com:8443/a#frag")]
    public void TryNormalizeUrl_accepts_and_canonicalises_http_urls(string input, string expected)
    {
        LinkRules.TryNormalizeUrl(input, BlockedHosts, out var normalized, out var error).ShouldBeTrue(error);

        normalized.ShouldBe(expected);
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("   ", "required")]
    [InlineData("example.com", "absolute")]
    [InlineData("/relative/path", "absolute")]
    [InlineData("ftp://example.com/file", "absolute")]
    [InlineData("javascript:alert(1)", "absolute")]
    [InlineData("mailto:someone@example.com", "absolute")]
    [InlineData("https://user:pass@example.com", "credentials")]
    [InlineData("https://sho.rt/abc1234", "this shortener")]
    [InlineData("https://SHO.RT/abc1234", "this shortener")]
    [InlineData("http://localhost:5000/x", "this shortener")]
    public void TryNormalizeUrl_rejects_unsafe_or_invalid_urls(string? input, string reason)
    {
        LinkRules.TryNormalizeUrl(input, BlockedHosts, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain(reason, Case.Insensitive);
    }

    [Fact]
    public void TryNormalizeUrl_enforces_the_length_limit()
    {
        var atLimit = "https://example.com/" + new string('a', LinkRules.MaxUrlLength - 20);
        var overLimit = atLimit + "a";

        LinkRules.TryNormalizeUrl(atLimit, BlockedHosts, out _, out _).ShouldBeTrue();
        LinkRules.TryNormalizeUrl(overLimit, BlockedHosts, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("at most");
    }

    [Fact]
    public void TryNormalizeUrl_rejects_urls_that_grow_past_the_limit_when_escaped()
    {
        // 700 non-ASCII characters is under the raw limit but triples when percent-encoded.
        var input = "https://example.com/" + new string('é', 700);

        LinkRules.TryNormalizeUrl(input, BlockedHosts, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("at most");
    }

    [Fact]
    public void Reserved_words_are_all_valid_code_shapes()
    {
        // Otherwise reserving them would be pointless.
        LinkRules.ReservedWords.ShouldAllBe(word => LinkRules.IsValidCodeFormat(word));
    }
}
