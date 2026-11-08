using System.Net.Mail;

namespace UrlShortener.Core.Users;

public enum RegisterUserStatus
{
    Created,
    Invalid,
    EmailTaken,
}

public sealed record RegisterUserResult(
    RegisterUserStatus Status,
    User? User = null,
    string? ApiKey = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed class UserService(IUserRepository repository, TimeProvider timeProvider)
{
    public const int MaxNameLength = 100;
    public const int MaxEmailLength = 254;

    public async Task<RegisterUserResult> RegisterAsync(string? name, string? email, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var trimmedName = name?.Trim() ?? string.Empty;
        var normalizedEmail = email?.Trim().ToLowerInvariant() ?? string.Empty;

        if (trimmedName.Length is 0 or > MaxNameLength)
        {
            errors["name"] = [$"Name is required and must be at most {MaxNameLength} characters."];
        }
        if (normalizedEmail.Length > MaxEmailLength ||
            !MailAddress.TryCreate(normalizedEmail, out var parsed) ||
            parsed.Address != normalizedEmail)
        {
            errors["email"] = ["A valid email address is required."];
        }
        if (errors.Count > 0)
        {
            return new RegisterUserResult(RegisterUserStatus.Invalid, Errors: errors);
        }

        var id = Guid.CreateVersion7().ToString("N");
        var (apiKey, secretHash) = ApiKeys.Create(id);
        var user = new User(id, trimmedName, normalizedEmail, timeProvider.GetUtcNow(), secretHash);

        if (!await repository.TryCreateAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return new RegisterUserResult(RegisterUserStatus.EmailTaken, Errors: new Dictionary<string, string[]>
            {
                ["email"] = ["This email is already registered."],
            });
        }

        return new RegisterUserResult(RegisterUserStatus.Created, user, apiKey);
    }
}
