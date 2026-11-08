using FsCheck;
using FsCheck.Xunit;
using UrlShortener.Core.ShortCodes;

namespace UrlShortener.Core.Tests.ShortCodes;

public sealed class Base62Tests
{
    private static readonly ulong SevenCharSpace = Base62.KeySpace(7);

    [Fact]
    public void Alphabet_has_62_unique_url_safe_characters()
    {
        Base62.Alphabet.Length.ShouldBe(62);
        Base62.Alphabet.Distinct().Count().ShouldBe(62);
        Base62.Alphabet.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c));
    }

    [Theory]
    [InlineData(1, 62UL)]
    [InlineData(6, 56_800_235_584UL)]
    [InlineData(7, 3_521_614_606_208UL)]
    [InlineData(10, 839_299_365_868_340_224UL)]
    public void KeySpace_is_62_to_the_power_of_length(int length, ulong expected) =>
        Base62.KeySpace(length).ShouldBe(expected);

    [Fact]
    public void Seven_characters_cover_100_years_of_traffic_but_six_do_not()
    {
        const ulong linksIn100Years = 100_000_000UL * 12 * 100; // 120 billion

        Base62.KeySpace(6).ShouldBeLessThan(linksIn100Years);
        Base62.KeySpace(7).ShouldBeGreaterThan(linksIn100Years);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void KeySpace_rejects_lengths_outside_1_to_10(int length) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Base62.KeySpace(length));

    [Theory]
    [InlineData(0UL, "0000000")]
    [InlineData(61UL, "000000z")]
    [InlineData(62UL, "0000010")]
    [InlineData(3_521_614_606_207UL, "zzzzzzz")]
    public void Encode_produces_fixed_length_left_padded_codes(ulong value, string expected) =>
        Base62.Encode(value, 7).ShouldBe(expected);

    [Fact]
    public void Encode_matches_the_textbook_example_ignoring_alphabet_order()
    {
        // 11157 = 2*62^2 + 55*62 + 59. The book's alphabet puts lowercase first ("2TX");
        // ours puts uppercase first, so the same digits [2, 55, 59] read "2tx".
        Base62.Encode(11157, 3).ShouldBe("2tx");
    }

    [Fact]
    public void Encode_rejects_values_that_do_not_fit_the_length() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Base62.Encode(SevenCharSpace, 7));

    [Theory]
    [InlineData("")]
    [InlineData("abc-def")]
    [InlineData("abc def")]
    [InlineData("ñandu")]
    [InlineData("12345678901")]
    public void Decode_rejects_malformed_input(string code) =>
        Should.Throw<FormatException>(() => Base62.Decode(code));

    [Theory]
    [InlineData("aZ09", true)]
    [InlineData("", true)]
    [InlineData("a_b", false)]
    [InlineData("a.b", false)]
    public void IsBase62_accepts_only_alphabet_characters(string value, bool expected) =>
        Base62.IsBase62(value).ShouldBe(expected);

    [Property(MaxTest = 2_000)]
    public bool Decode_inverts_Encode_across_the_whole_7_char_space(DoNotSize<ulong> raw)
    {
        var value = raw.Item % SevenCharSpace;
        var code = Base62.Encode(value, 7);

        return code.Length == 7 && Base62.IsBase62(code) && Base62.Decode(code) == value;
    }

    [Property(MaxTest = 500)]
    public bool Encoding_preserves_numeric_order(DoNotSize<ulong> a, DoNotSize<ulong> b)
    {
        var (x, y) = (a.Item % SevenCharSpace, b.Item % SevenCharSpace);

        // Fixed-length, digit-ordered alphabet => ordinal string order equals numeric order.
        return Math.Sign(string.CompareOrdinal(Base62.Encode(x, 7), Base62.Encode(y, 7))) == x.CompareTo(y);
    }
}
