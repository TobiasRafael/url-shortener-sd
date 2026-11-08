namespace UrlShortener.Core.ShortCodes;

/// <summary>
/// Base62 encoding using the URL-safe alphabet <c>[0-9A-Za-z]</c>.
/// </summary>
public static class Base62
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Radix = 62;

    /// <summary>Returns 62^<paramref name="length"/>, the number of distinct codes of that length.</summary>
    public static ulong KeySpace(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 10); // 62^10 fits in a ulong, 62^11 does not.

        ulong result = 1;
        for (var i = 0; i < length; i++)
        {
            result *= Radix;
        }
        return result;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as exactly <paramref name="length"/> characters, left-padded with '0'.
    /// </summary>
    public static string Encode(ulong value, int length)
    {
        if (value >= KeySpace(length))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Value does not fit in {length} base62 characters.");
        }

        return string.Create(length, value, static (span, v) =>
        {
            for (var i = span.Length - 1; i >= 0; i--)
            {
                span[i] = Alphabet[(int)(v % Radix)];
                v /= Radix;
            }
        });
    }

    public static ulong Decode(ReadOnlySpan<char> code)
    {
        if (code.IsEmpty || code.Length > 10)
        {
            throw new FormatException("A base62 code must be between 1 and 10 characters long.");
        }

        ulong result = 0;
        foreach (var c in code)
        {
            var digit = DigitOf(c);
            if (digit < 0)
            {
                throw new FormatException($"'{c}' is not a base62 character.");
            }
            result = checked((result * Radix) + (ulong)digit);
        }
        return result;
    }

    public static bool IsBase62(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (DigitOf(c) < 0)
            {
                return false;
            }
        }
        return true;
    }

    private static int DigitOf(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 36,
        _ => -1,
    };
}
