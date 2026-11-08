using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Core.ShortCodes;

/// <summary>
/// A keyed, reversible permutation of the integers <c>[0, domainSize)</c>.
/// </summary>
/// <remarks>
/// Sequential counter values (0, 1, 2, ...) would produce guessable, enumerable short codes
/// (<c>0000001</c>, <c>0000002</c>, ...). Pushing each counter value through a balanced Feistel
/// network scrambles it into a value that looks random but is still <b>guaranteed unique</b>,
/// because a Feistel network is a bijection whatever round function it uses.
/// <para>
/// The network works on <c>2k</c> bits, the smallest even width that covers the domain. Results
/// that land outside the domain are fed through the network again ("cycle walking"). That keeps
/// the mapping a bijection on exactly <c>[0, domainSize)</c>, so every counter value still maps
/// to a valid fixed-length code. For the 62^7 domain, the 42-bit network wastes about 20% of
/// its range, so a lookup needs about 1.25 iterations on average.
/// </para>
/// This is obfuscation, not encryption: it stops casual enumeration of links and does not
/// protect against a determined cryptanalyst.
/// </remarks>
public sealed class FeistelPermutation
{
    private const int Rounds = 4;

    private readonly ulong _domainSize;
    private readonly int _halfBits;
    private readonly ulong _halfMask;
    private readonly ulong[] _roundKeys;

    public FeistelPermutation(ulong domainSize, string secret)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(domainSize, 2UL);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        _domainSize = domainSize;

        var bits = 64 - BitOperations.LeadingZeroCount(domainSize - 1);
        if (bits % 2 != 0)
        {
            bits++;
        }
        if (bits > 62)
        {
            throw new ArgumentOutOfRangeException(nameof(domainSize), "Domain is too large for this permutation.");
        }

        _halfBits = bits / 2;
        _halfMask = (1UL << _halfBits) - 1;
        _roundKeys = DeriveRoundKeys(secret);
    }

    public ulong DomainSize => _domainSize;

    public ulong Permute(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, _domainSize);

        do
        {
            value = Encrypt(value);
        }
        while (value >= _domainSize);

        return value;
    }

    public ulong Inverse(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, _domainSize);

        do
        {
            value = Decrypt(value);
        }
        while (value >= _domainSize);

        return value;
    }

    private ulong Encrypt(ulong value)
    {
        var left = value >> _halfBits;
        var right = value & _halfMask;

        for (var i = 0; i < Rounds; i++)
        {
            (left, right) = (right, left ^ RoundFunction(right, _roundKeys[i]));
        }

        return (left << _halfBits) | right;
    }

    private ulong Decrypt(ulong value)
    {
        var left = value >> _halfBits;
        var right = value & _halfMask;

        for (var i = Rounds - 1; i >= 0; i--)
        {
            (left, right) = (right ^ RoundFunction(left, _roundKeys[i]), left);
        }

        return (left << _halfBits) | right;
    }

    /// <summary>SplitMix64 finaliser over the keyed input: cheap, with good avalanche behaviour.</summary>
    private ulong RoundFunction(ulong half, ulong key)
    {
        var z = half ^ key;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return z & _halfMask;
    }

    private static ulong[] DeriveRoundKeys(string secret)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(secret), hash);

        var keys = new ulong[Rounds];
        for (var i = 0; i < Rounds; i++)
        {
            keys[i] = BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(i * sizeof(ulong), sizeof(ulong)));
        }
        return keys;
    }
}
