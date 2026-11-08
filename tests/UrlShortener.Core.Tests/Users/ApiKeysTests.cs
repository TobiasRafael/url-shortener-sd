using FsCheck;
using FsCheck.Xunit;
using UrlShortener.Core.Users;

namespace UrlShortener.Core.Tests.Users;

public sealed class ApiKeysTests
{
    private const string UserId = "0192f1a2b3c47d8e9f00112233445566";

    [Fact]
    public void Create_returns_a_prefixed_key_that_parses_back_to_the_user()
    {
        var (apiKey, _) = ApiKeys.Create(UserId);

        apiKey.ShouldStartWith(ApiKeys.Prefix);
        ApiKeys.TryParse(apiKey, out var userId, out var secret).ShouldBeTrue();
        userId.ShouldBe(UserId);
        secret.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void The_stored_hash_verifies_the_secret_but_never_contains_it()
    {
        var (apiKey, hash) = ApiKeys.Create(UserId);
        ApiKeys.TryParse(apiKey, out _, out var secret).ShouldBeTrue();

        ApiKeys.Verify(secret, hash).ShouldBeTrue();
        hash.ShouldNotContain(secret);
        hash.Length.ShouldBe(64); // SHA-256 hex
    }

    [Fact]
    public void Verify_rejects_a_wrong_secret()
    {
        var (_, hash) = ApiKeys.Create(UserId);
        var (otherKey, _) = ApiKeys.Create(UserId);
        ApiKeys.TryParse(otherKey, out _, out var otherSecret).ShouldBeTrue();

        ApiKeys.Verify(otherSecret, hash).ShouldBeFalse();
    }

    [Fact]
    public void Secrets_are_url_safe_and_unique()
    {
        var secrets = Enumerable.Range(0, 1_000).Select(i =>
        {
            ApiKeys.TryParse(ApiKeys.Create(UserId).ApiKey, out _, out var secret).ShouldBeTrue();
            return secret;
        }).ToList();

        secrets.Distinct().Count().ShouldBe(1_000);
        secrets.ShouldAllBe(s => s.Length == 43 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0192f1a2.secret")]       // missing prefix
    [InlineData("sk_0192f1a2.secret")]    // wrong prefix
    [InlineData("usk_")]                  // no body
    [InlineData("usk_nodot")]             // no separator
    [InlineData("usk_.secret")]           // empty user id
    [InlineData("usk_0192f1a2.")]         // empty secret
    public void TryParse_rejects_malformed_keys(string? apiKey) =>
        ApiKeys.TryParse(apiKey, out _, out _).ShouldBeFalse();

    [Property(MaxTest = 200)]
    public bool Any_hex_user_id_round_trips(Guid id)
    {
        var userId = id.ToString("N");
        var (apiKey, hash) = ApiKeys.Create(userId);

        return ApiKeys.TryParse(apiKey, out var parsedId, out var secret)
            && parsedId == userId
            && ApiKeys.Verify(secret, hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_requires_a_user_id(string userId) =>
        Should.Throw<ArgumentException>(() => ApiKeys.Create(userId));
}
