using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Cosmos;
using UrlShortener.Core.Links;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Users;
using User = UrlShortener.Core.Users.User;

namespace UrlShortener.Infrastructure.Cosmos;

// The hot paths use the stream APIs on purpose: a "not found" or "conflict" comes back as a
// status code, not an exception. That matters on the redirect path, where bots probing
// random codes produce a steady stream of 404s.

public sealed class CosmosLinkRepository(CosmosContainers containers) : ILinkRepository
{
    public async Task<bool> TryCreateAsync(ShortLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        using var body = CosmosJson.ToStream(LinkDocument.From(link));
        using var response = await containers.Links
            .CreateItemStreamAsync(body, new PartitionKey(link.Code), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<ShortLink?> GetAsync(string code, CancellationToken cancellationToken)
    {
        if (!CosmosIds.IsValid(code))
        {
            return null;
        }

        using var response = await containers.Links
            .ReadItemStreamAsync(code, new PartitionKey(code), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return (await CosmosJson.ReadAsync<LinkDocument>(response, cancellationToken).ConfigureAwait(false)).ToModel();
    }

    public async Task<IReadOnlyList<ShortLink>> GetByUserAsync(string userId, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Cross-partition, but served by the userId/createdAt index. At very large scale a
        // container partitioned by /userId (fed by the change feed) would make this single-partition.
        var query = new QueryDefinition(
                $"SELECT TOP {limit.ToString(CultureInfo.InvariantCulture)} * FROM c WHERE c.userId = @userId ORDER BY c.createdAt DESC")
            .WithParameter("@userId", userId);

        var links = new List<ShortLink>(limit);
        using var iterator = containers.Links.GetItemQueryIterator<LinkDocument>(query, requestOptions: new QueryRequestOptions { MaxItemCount = limit });
        while (iterator.HasMoreResults && links.Count < limit)
        {
            foreach (var document in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                links.Add(document.ToModel());
            }
        }
        return links;
    }
}

public sealed class CosmosDestinationIndex(CosmosContainers containers) : IDestinationIndex
{
    public async Task<string?> FindCodeAsync(string destinationKey, CancellationToken cancellationToken)
    {
        using var response = await containers.Destinations
            .ReadItemStreamAsync(destinationKey, new PartitionKey(destinationKey), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return (await CosmosJson.ReadAsync<DestinationDocument>(response, cancellationToken).ConfigureAwait(false)).Code;
    }

    public async Task<string> AddIfAbsentAsync(string destinationKey, string code, CancellationToken cancellationToken)
    {
        using (var body = CosmosJson.ToStream(new DestinationDocument(destinationKey, code)))
        using (var response = await containers.Destinations
            .CreateItemStreamAsync(body, new PartitionKey(destinationKey), cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode)
            {
                return code;
            }
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                response.EnsureSuccessStatusCode();
            }
        }

        // Someone else indexed this destination first: theirs is the canonical code.
        return await FindCodeAsync(destinationKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Destination index entry disappeared after a conflict.");
    }
}

public sealed class CosmosUserRepository(CosmosContainers containers) : IUserRepository
{
    public async Task<bool> TryCreateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        // 1. Claim the email. The id is unique per container, so this is the uniqueness check.
        var claim = new EmailClaimDocument(EmailClaimId(user.Email), user.Id);
        using (var body = CosmosJson.ToStream(claim))
        using (var claimResponse = await containers.Users
            .CreateItemStreamAsync(body, new PartitionKey(claim.Id), cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            if (claimResponse.StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }
            claimResponse.EnsureSuccessStatusCode();
        }

        // 2. Create the user. The two documents live in different partitions, so this can't be one
        //    transaction; if step 2 fails, release the claim so the email isn't locked out.
        try
        {
            using var body = CosmosJson.ToStream(UserDocument.From(user));
            using var userResponse = await containers.Users
                .CreateItemStreamAsync(body, new PartitionKey(user.Id), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (userResponse.StatusCode == HttpStatusCode.Conflict)
            {
                await ReleaseClaimAsync(claim.Id).ConfigureAwait(false);
                return false;
            }
            userResponse.EnsureSuccessStatusCode();
            return true;
        }
        catch
        {
            await ReleaseClaimAsync(claim.Id).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<User?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!CosmosIds.IsValid(id))
        {
            return null;
        }

        using var response = await containers.Users
            .ReadItemStreamAsync(id, new PartitionKey(id), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        var document = await CosmosJson.ReadAsync<UserDocument>(response, cancellationToken).ConfigureAwait(false);
        return document.Type == UserDocument.UserType ? document.ToModel() : null; // never hand out an email claim
    }

    internal static string EmailClaimId(string email) =>
        "email-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email)));

    private async Task ReleaseClaimAsync(string claimId)
    {
        using var _ = await containers.Users
            .DeleteItemStreamAsync(claimId, new PartitionKey(claimId), cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The "ticket server": one counter document, advanced atomically with a server-side
/// <c>Increment</c> patch. Each call leases a whole block, so the counter is written once per
/// <c>size</c> links, not once per link, and the patch response says where the block ends.
/// </summary>
public sealed class CosmosIdRangeAllocator(CosmosContainers containers) : IIdRangeAllocator
{
    internal const string CounterId = "links";
    private const int MaxAttempts = 3;

    public async Task<IdRange> AllocateAsync(int size, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var partitionKey = new PartitionKey(CounterId);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            using (var response = await containers.Counters
                .PatchItemStreamAsync(CounterId, partitionKey, [PatchOperation.Increment("/next", size)], cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    var counter = await CosmosJson.ReadAsync<CounterDocument>(response, cancellationToken).ConfigureAwait(false);
                    return new IdRange(counter.Next - size, counter.Next);
                }
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    response.EnsureSuccessStatusCode();
                }
            }

            // First allocation ever: create the counter at 0 (losing a creation race is fine).
            using var body = CosmosJson.ToStream(new CounterDocument(CounterId, 0));
            using var created = await containers.Counters
                .CreateItemStreamAsync(body, partitionKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.Conflict)
            {
                created.EnsureSuccessStatusCode();
            }
        }

        throw new InvalidOperationException("Could not allocate an ID range.");
    }
}
