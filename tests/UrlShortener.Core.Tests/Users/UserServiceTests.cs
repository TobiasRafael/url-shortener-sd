using Microsoft.Extensions.Time.Testing;
using UrlShortener.Core.Tests.TestDoubles;
using UrlShortener.Core.Users;

namespace UrlShortener.Core.Tests.Users;

public sealed class UserServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _repository = new();
    private readonly UserService _service;

    public UserServiceTests() => _service = new UserService(_repository, new FakeTimeProvider(Now));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registers_a_user_and_issues_a_working_api_key()
    {
        var result = await _service.RegisterAsync("  Ada Lovelace ", " Ada@Example.COM ", Ct);

        result.Status.ShouldBe(RegisterUserStatus.Created);
        var user = result.User.ShouldNotBeNull();
        user.Name.ShouldBe("Ada Lovelace");
        user.Email.ShouldBe("ada@example.com");
        user.CreatedAt.ShouldBe(Now);
        Guid.TryParseExact(user.Id, "N", out _).ShouldBeTrue();

        ApiKeys.TryParse(result.ApiKey, out var keyUserId, out var secret).ShouldBeTrue();
        keyUserId.ShouldBe(user.Id);
        ApiKeys.Verify(secret, user.ApiKeyHash).ShouldBeTrue();
        (await _repository.GetAsync(user.Id, Ct)).ShouldBe(user);
    }

    [Fact]
    public async Task Rejects_a_duplicate_email_regardless_of_case()
    {
        await _service.RegisterAsync("Ada", "ada@example.com", Ct);

        var result = await _service.RegisterAsync("Imposter", "ADA@example.com", Ct);

        result.Status.ShouldBe(RegisterUserStatus.EmailTaken);
        result.ApiKey.ShouldBeNull();
        result.Errors.ShouldNotBeNull().ShouldContainKey("email");
    }

    [Theory]
    [InlineData(null, "ada@example.com", "name")]
    [InlineData("   ", "ada@example.com", "name")]
    [InlineData("Ada", null, "email")]
    [InlineData("Ada", "not-an-email", "email")]
    [InlineData("Ada", "Ada Lovelace <ada@example.com>", "email")] // display-name form is not an address
    [InlineData("Ada", "ada@", "email")]
    public async Task Rejects_invalid_input(string? name, string? email, string field)
    {
        var result = await _service.RegisterAsync(name, email, Ct);

        result.Status.ShouldBe(RegisterUserStatus.Invalid);
        result.Errors.ShouldNotBeNull().ShouldContainKey(field);
    }

    [Fact]
    public async Task Reports_every_invalid_field_at_once()
    {
        var result = await _service.RegisterAsync("", "nope", Ct);

        result.Errors.ShouldNotBeNull().Keys.ShouldBe(["name", "email"], ignoreOrder: true);
    }

    [Fact]
    public async Task Enforces_length_limits()
    {
        var longName = new string('a', UserService.MaxNameLength + 1);
        var longEmail = new string('a', UserService.MaxEmailLength) + "@example.com";

        (await _service.RegisterAsync(longName, "ok@example.com", Ct)).Errors.ShouldNotBeNull().ShouldContainKey("name");
        (await _service.RegisterAsync("Ada", longEmail, Ct)).Errors.ShouldNotBeNull().ShouldContainKey("email");
    }

    [Fact]
    public async Task Each_registration_gets_a_distinct_id_and_key()
    {
        var a = await _service.RegisterAsync("A", "a@example.com", Ct);
        var b = await _service.RegisterAsync("B", "b@example.com", Ct);

        a.User!.Id.ShouldNotBe(b.User!.Id);
        a.ApiKey.ShouldNotBe(b.ApiKey);
    }
}
