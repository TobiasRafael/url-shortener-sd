namespace UrlShortener.Core.Users;

public sealed record User(
    string Id,
    string Name,
    string Email,
    DateTimeOffset CreatedAt,
    string ApiKeyHash);

public interface IUserRepository
{
    /// <summary>Inserts the user. Returns <c>false</c> if the email is already registered.</summary>
    Task<bool> TryCreateAsync(User user, CancellationToken cancellationToken);

    Task<User?> GetAsync(string id, CancellationToken cancellationToken);
}
