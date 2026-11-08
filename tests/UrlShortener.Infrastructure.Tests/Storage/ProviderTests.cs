using System.Net;
using Microsoft.Azure.Cosmos;
using UrlShortener.Infrastructure.Cosmos;
using UrlShortener.Infrastructure.InMemory;
using UrlShortener.Infrastructure.Tests.Cosmos;

namespace UrlShortener.Infrastructure.Tests.Storage;

public sealed class InMemoryStorageTests : StorageContractTests
{
    protected override Task<Stores> CreateStoresAsync() => Task.FromResult(new Stores(
        new InMemoryLinkRepository(),
        new InMemoryDestinationIndex(),
        new InMemoryUserRepository(),
        new InMemoryIdRangeAllocator(),
        new InMemoryStatsStore()));
}

public sealed class CosmosStorageTests(CosmosEmulatorFixture emulator) : StorageContractTests
{
    protected override async Task<Stores> CreateStoresAsync()
    {
        var containers = await emulator.CreateDatabaseAsync(Ct);
        return new Stores(
            new CosmosLinkRepository(containers),
            new CosmosDestinationIndex(containers),
            new CosmosUserRepository(containers),
            new CosmosIdRangeAllocator(containers),
            new CosmosStatsStore(containers));
    }

    // ---- Cosmos-specific behaviour, beyond the shared contract ---------------------------

    [Fact]
    public async Task Schema_creates_every_container_partitioned_by_id()
    {
        var containers = await emulator.CreateDatabaseAsync(Ct);

        foreach (var container in new[] { containers.Links, containers.Destinations, containers.Users, containers.Counters, containers.LinkStats })
        {
            var properties = (await container.ReadContainerAsync(cancellationToken: Ct)).Resource;
            properties.PartitionKeyPath.ShouldBe("/id");
        }
    }

    [Fact]
    public async Task Schema_creation_is_idempotent()
    {
        var containers = await emulator.CreateDatabaseAsync(Ct);

        await CosmosSchema.EnsureCreatedAsync(emulator.Client!, containers.Links.Database.Id, Ct);
    }

    [Fact]
    public async Task Users_email_claims_are_never_returned_as_users()
    {
        var containers = await emulator.CreateDatabaseAsync(Ct);
        var users = new CosmosUserRepository(containers);
        await users.TryCreateAsync(new("0192f1a2b3c47d8e9f00112233445566", "Ada", "ada@example.com", T0, new string('A', 64)), Ct);

        (await users.GetAsync(CosmosUserRepository.EmailClaimId("ada@example.com"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Users_email_claims_store_a_hash_not_the_address()
    {
        var containers = await emulator.CreateDatabaseAsync(Ct);
        var users = new CosmosUserRepository(containers);
        await users.TryCreateAsync(new("0192f1a2b3c47d8e9f00112233445566", "Ada", "ada@example.com", T0, new string('A', 64)), Ct);

        var claimId = CosmosUserRepository.EmailClaimId("ada@example.com");
        using var response = await containers.Users.ReadItemStreamAsync(claimId, new PartitionKey(claimId), cancellationToken: Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        claimId.ShouldNotContain("ada");
    }

    [Fact]
    public async Task Allocator_survives_losing_the_counter_creation_race()
    {
        var containers = await emulator.CreateDatabaseAsync(Ct);
        var replicas = Enumerable.Range(0, 5).Select(_ => new CosmosIdRangeAllocator(containers)).ToArray();

        // Five "replicas" boot at once against an empty counters container.
        var ranges = await Task.WhenAll(replicas.Select(r => r.AllocateAsync(1_000, Ct)));

        ranges.Select(r => r.Start).Order().ShouldBe([0L, 1_000L, 2_000L, 3_000L, 4_000L]);
    }
}
