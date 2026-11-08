using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Core.Users;

/// <summary>
/// API keys have the form <c>usk_{userId}.{secret}</c>. The user ID lets the server point-read
/// the user record directly, without a secondary index. Only a SHA-256 hash of the secret is
/// stored, and it is compared in constant time.
/// </summary>
public static class ApiKeys
{
    public const string Prefix = "usk_";
    private const int SecretBytes = 32;

    public static (string ApiKey, string SecretHash) Create(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretBytes));
        return ($"{Prefix}{userId}.{secret}", Hash(secret));
    }

    public static bool TryParse(string? apiKey, [NotNullWhen(true)] out string? userId, [NotNullWhen(true)] out string? secret)
    {
        userId = secret = null;
        if (string.IsNullOrEmpty(apiKey) || !apiKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = apiKey.AsSpan(Prefix.Length);
        var dot = body.IndexOf('.');
        if (dot <= 0 || dot == body.Length - 1)
        {
            return false;
        }

        userId = body[..dot].ToString();
        secret = body[(dot + 1)..].ToString();
        return true;
    }

    public static bool Verify(string secret, string expectedHash)
    {
        var actual = Encoding.ASCII.GetBytes(Hash(secret));
        var expected = Encoding.ASCII.GetBytes(expectedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
