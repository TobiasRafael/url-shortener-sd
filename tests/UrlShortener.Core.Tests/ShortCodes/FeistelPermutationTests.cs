using FsCheck;
using FsCheck.Xunit;
using UrlShortener.Core.ShortCodes;

namespace UrlShortener.Core.Tests.ShortCodes;

public sealed class FeistelPermutationTests
{
    private const string Secret = "test-secret";
    private static readonly FeistelPermutation SevenChar = new(Base62.KeySpace(7), Secret);

    [Theory]
    [InlineData(238_328UL)] // 62^3: 18-bit network
    [InlineData(1_000UL)]   // 10-bit network, ~2% cycle walking
    [InlineData(1_025UL)]   // just over a power of two: odd bit count gets rounded up, heavy cycle walking
    [InlineData(2UL)]       // smallest domain
    public void Is_a_bijection_over_the_entire_domain(ulong domainSize)
    {
        var permutation = new FeistelPermutation(domainSize, Secret);
        var outputs = new bool[domainSize];

        for (ulong i = 0; i < domainSize; i++)
        {
            var output = permutation.Permute(i);

            output.ShouldBeLessThan(domainSize);
            outputs[output].ShouldBeFalse($"{output} was produced twice");
            outputs[output] = true;
            permutation.Inverse(output).ShouldBe(i);
        }
    }

    [Property(MaxTest = 2_000)]
    public bool Inverse_undoes_Permute_in_the_7_char_domain(DoNotSize<ulong> raw)
    {
        var value = raw.Item % SevenChar.DomainSize;
        var permuted = SevenChar.Permute(value);

        return permuted < SevenChar.DomainSize && SevenChar.Inverse(permuted) == value;
    }

    [Fact]
    public void Same_secret_gives_the_same_mapping()
    {
        var other = new FeistelPermutation(Base62.KeySpace(7), Secret);

        for (ulong i = 0; i < 100; i++)
        {
            other.Permute(i).ShouldBe(SevenChar.Permute(i));
        }
    }

    [Fact]
    public void Different_secrets_give_different_mappings()
    {
        var other = new FeistelPermutation(Base62.KeySpace(7), "another-secret");

        Enumerable.Range(0, 100).Count(i => other.Permute((ulong)i) == SevenChar.Permute((ulong)i))
            .ShouldBeLessThan(2);
    }

    [Fact]
    public void Consecutive_inputs_are_scattered_across_the_domain()
    {
        // The whole point: counter values 0..999 must not produce neighbouring codes.
        var outputs = Enumerable.Range(0, 1_000).Select(i => SevenChar.Permute((ulong)i)).Order().ToArray();

        var adjacentPairs = outputs.Zip(outputs.Skip(1)).Count(p => p.Second - p.First == 1);
        adjacentPairs.ShouldBe(0);

        // Spread: outputs should reach both the bottom and the top tenth of the space.
        var tenth = SevenChar.DomainSize / 10;
        outputs.ShouldContain(o => o < tenth);
        outputs.ShouldContain(o => o > SevenChar.DomainSize - tenth);
    }

    [Fact]
    public void Rejects_values_outside_the_domain()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SevenChar.Permute(SevenChar.DomainSize));
        Should.Throw<ArgumentOutOfRangeException>(() => SevenChar.Inverse(SevenChar.DomainSize));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    public void Rejects_degenerate_domains(ulong domainSize) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new FeistelPermutation(domainSize, Secret));

    [Fact]
    public void Rejects_domains_wider_than_62_bits() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new FeistelPermutation(1UL << 63, Secret));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Requires_a_secret(string secret) =>
        Should.Throw<ArgumentException>(() => new FeistelPermutation(1_000, secret));
}
