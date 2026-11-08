using UrlShortener.Core.ShortCodes;

namespace UrlShortener.Core.Links;

public sealed record CreateLinkCommand(string? LongUrl, string? CustomAlias, string? UserId);

public enum CreateLinkStatus
{
    /// <summary>A new short link was created.</summary>
    Created,

    /// <summary>The owner had already shortened this URL; the existing link is returned.</summary>
    Existing,

    Invalid,
    AliasTaken,
}

public sealed record CreateLinkResult(CreateLinkStatus Status, ShortLink? Link = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static CreateLinkResult Success(ShortLink link) => new(CreateLinkStatus.Created, link);

    public static CreateLinkResult Found(ShortLink link) => new(CreateLinkStatus.Existing, link);

    public static CreateLinkResult Invalid(string field, string message) =>
        new(CreateLinkStatus.Invalid, Errors: new Dictionary<string, string[]> { [field] = [message] });

    public static CreateLinkResult Taken(string alias) =>
        new(CreateLinkStatus.AliasTaken, Errors: new Dictionary<string, string[]>
        {
            ["customAlias"] = [$"The alias '{alias}' is already taken."],
        });
}

public sealed class LinkServiceSettings
{
    /// <summary>Hosts that may not be shortened, usually the shortener's own domains.</summary>
    public IReadOnlySet<string> BlockedHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shortening flow:
/// <list type="number">
///   <item>Validate and normalise the long URL.</item>
///   <item>Custom alias: claim it with an insert-if-absent write. No deduplication, because
///         asking for an alias means asking for a new link.</item>
///   <item>Otherwise, if the owner has already shortened this URL, return that link.</item>
///   <item>Otherwise, generate a code (unique ID → Feistel → base62), store the link, then
///         record it in the destination index.</item>
/// </list>
/// </summary>
public sealed class LinkService(
    ILinkRepository repository,
    IDestinationIndex destinationIndex,
    IShortCodeGenerator generator,
    TimeProvider timeProvider,
    LinkServiceSettings settings)
{
    // A generated code only collides when someone has already claimed it as a custom
    // alias, which is vanishingly rare, so a handful of retries is more than enough.
    private const int MaxGenerationAttempts = 5;

    public async Task<CreateLinkResult> CreateAsync(CreateLinkCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!LinkRules.TryNormalizeUrl(command.LongUrl, settings.BlockedHosts, out var longUrl, out var urlError))
        {
            return CreateLinkResult.Invalid("longUrl", urlError!);
        }

        var now = timeProvider.GetUtcNow();
        var alias = command.CustomAlias?.Trim();

        if (!string.IsNullOrEmpty(alias))
        {
            return await CreateCustomAsync(alias, longUrl, now, command.UserId, cancellationToken).ConfigureAwait(false);
        }

        var destinationKey = DestinationKey.For(command.UserId, longUrl);

        if (await destinationIndex.FindCodeAsync(destinationKey, cancellationToken).ConfigureAwait(false) is { } existingCode &&
            await repository.GetAsync(existingCode, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return CreateLinkResult.Found(existing);
        }

        var link = await CreateGeneratedAsync(longUrl, now, command.UserId, cancellationToken).ConfigureAwait(false);

        // Write the index only after the link exists, so it never points at a missing link.
        // If two identical requests race, both create a link but only one is indexed. Return
        // that winner so both callers get the same code. The loser's link stays valid, but
        // nobody is given it.
        var indexedCode = await destinationIndex.AddIfAbsentAsync(destinationKey, link.Code, cancellationToken).ConfigureAwait(false);
        if (indexedCode != link.Code &&
            await repository.GetAsync(indexedCode, cancellationToken).ConfigureAwait(false) is { } winner)
        {
            return CreateLinkResult.Found(winner);
        }

        return CreateLinkResult.Success(link);
    }

    private async Task<CreateLinkResult> CreateCustomAsync(
        string alias, string longUrl, DateTimeOffset now, string? userId, CancellationToken cancellationToken)
    {
        if (LinkRules.ValidateAlias(alias) is { } aliasError)
        {
            return CreateLinkResult.Invalid("customAlias", aliasError);
        }

        var custom = new ShortLink(alias, longUrl, now, userId, IsCustom: true);
        return await repository.TryCreateAsync(custom, cancellationToken).ConfigureAwait(false)
            ? CreateLinkResult.Success(custom)
            : CreateLinkResult.Taken(alias);
    }

    private async Task<ShortLink> CreateGeneratedAsync(
        string longUrl, DateTimeOffset now, string? userId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxGenerationAttempts; attempt++)
        {
            var code = await generator.NextAsync(cancellationToken).ConfigureAwait(false);
            if (LinkRules.IsReserved(code))
            {
                continue;
            }

            var link = new ShortLink(code, longUrl, now, userId, IsCustom: false);
            if (await repository.TryCreateAsync(link, cancellationToken).ConfigureAwait(false))
            {
                return link;
            }
        }

        throw new InvalidOperationException($"Could not allocate a unique short code after {MaxGenerationAttempts} attempts.");
    }
}
