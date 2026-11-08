using UrlShortener.Core.Links;

namespace UrlShortener.Core.Tests.Links;

public sealed class DestinationKeyTests
{
    private const string Url = "https://example.com/page";

    [Fact]
    public void Is_a_64_character_lowercase_hex_digest()
    {
        var key = DestinationKey.For("user-1", Url);

        key.Length.ShouldBe(64);
        key.ShouldAllBe(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c));
    }

    [Fact]
    public void Is_deterministic() =>
        DestinationKey.For("user-1", Url).ShouldBe(DestinationKey.For("user-1", Url));

    [Fact]
    public void Separates_users_from_each_other_and_from_anonymous()
    {
        string[] keys =
        [
            DestinationKey.For("user-1", Url),
            DestinationKey.For("user-2", Url),
            DestinationKey.For(null, Url),
        ];

        keys.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void Treats_null_and_empty_user_as_the_same_anonymous_scope() =>
        DestinationKey.For(string.Empty, Url).ShouldBe(DestinationKey.For(null, Url));

    [Fact]
    public void Cannot_be_spoofed_by_a_user_id_that_looks_like_the_anonymous_scope() =>
        DestinationKey.For("anon", Url).ShouldNotBe(DestinationKey.For(null, Url));

    [Fact]
    public void Distinguishes_urls() =>
        DestinationKey.For(null, Url).ShouldNotBe(DestinationKey.For(null, Url + "?x=1"));

    [Fact]
    public void Stays_fixed_length_for_maximum_length_urls() =>
        DestinationKey.For("user-1", "https://example.com/" + new string('a', 2_000)).Length.ShouldBe(64);
}
