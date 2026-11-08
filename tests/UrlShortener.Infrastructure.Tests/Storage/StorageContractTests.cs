using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Users;

namespace UrlShortener.Infrastructure.Tests.Storage;

public sealed record Stores(
    ILinkRepository Links,
    IDestinationIndex Destinations,
    IUserRepository Users,
    IIdRangeAllocator Allocator,
    IStatsStore Stats);

/// <summary>
/// One behavioural contract, run against every storage provider (in-memory and Cosmos DB).
/// Whichever provider is configured, the domain layer gets the same guarantees.
/// </summary>
public abstract class StorageContractTests
{
    protected static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Returns stores over fresh, empty storage.</summary>
    protected abstract Task<Stores> CreateStoresAsync();

    private static ShortLink Link(string code, string? userId = null, DateTimeOffset? createdAt = null, bool isCustom = false) =>
        new(code, $"https://example.com/{code}", createdAt ?? T0, userId, isCustom);

    // ---- Links ---------------------------------------------------------------------------

    [Fact]
    public async Task Links_round_trip_every_field()
    {
        var stores = await CreateStoresAsync();
        var link = new ShortLink("abc1234", "https://example.com/a?b=c&d=%C3%A9", T0.AddTicks(1234567), "user-1", IsCustom: true);

        (await stores.Links.TryCreateAsync(link, Ct)).ShouldBeTrue();

        (await stores.Links.GetAsync("abc1234", Ct)).ShouldBe(link);
    }

    [Fact]
    public async Task Links_never_overwrite_an_existing_code()
    {
        var stores = await CreateStoresAsync();
        await stores.Links.TryCreateAsync(Link("abc1234", "first"), Ct);

        (await stores.Links.TryCreateAsync(Link("abc1234", "second"), Ct)).ShouldBeFalse();

        (await stores.Links.GetAsync("abc1234", Ct)).ShouldNotBeNull().UserId.ShouldBe("first");
    }

    [Fact]
    public async Task Links_codes_are_case_sensitive()
    {
        var stores = await CreateStoresAsync();

        (await stores.Links.TryCreateAsync(Link("Promo"), Ct)).ShouldBeTrue();
        (await stores.Links.TryCreateAsync(Link("promo"), Ct)).ShouldBeTrue();
        (await stores.Links.GetAsync("PROMO", Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("has/slash")]
    [InlineData("has?query")]
    [InlineData("has#hash")]
    public async Task Links_unknown_or_unstorable_codes_read_as_null(string code)
    {
        var stores = await CreateStoresAsync();

        (await stores.Links.GetAsync(code, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Links_concurrent_creates_of_one_code_have_exactly_one_winner()
    {
        var stores = await CreateStoresAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => stores.Links.TryCreateAsync(Link("hot-code", $"user-{i}"), Ct)));

        results.Count(created => created).ShouldBe(1);
    }

    [Fact]
    public async Task Links_by_user_are_newest_first_limited_and_scoped()
    {
        var stores = await CreateStoresAsync();
        for (var i = 0; i < 5; i++)
        {
            await stores.Links.TryCreateAsync(Link($"mine{i}", "alice", T0.AddMinutes(i)), Ct);
        }
        await stores.Links.TryCreateAsync(Link("theirs", "bob", T0.AddHours(1)), Ct);
        await stores.Links.TryCreateAsync(Link("anon01", null, T0.AddHours(2)), Ct);

        var links = await stores.Links.GetByUserAsync("alice", limit: 3, Ct);

        links.Select(l => l.Code).ShouldBe(["mine4", "mine3", "mine2"]);
        (await stores.Links.GetByUserAsync("nobody", 10, Ct)).ShouldBeEmpty();
    }

    // ---- Destination index ---------------------------------------------------------------

    [Fact]
    public async Task Destinations_unknown_key_is_null()
    {
        var stores = await CreateStoresAsync();

        (await stores.Destinations.FindCodeAsync(DestinationKey.For(null, "https://example.com/"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Destinations_first_writer_wins_and_later_writers_learn_its_code()
    {
        var stores = await CreateStoresAsync();
        var key = DestinationKey.For("alice", "https://example.com/");

        (await stores.Destinations.AddIfAbsentAsync(key, "first01", Ct)).ShouldBe("first01");
        (await stores.Destinations.AddIfAbsentAsync(key, "second2", Ct)).ShouldBe("first01");
        (await stores.Destinations.FindCodeAsync(key, Ct)).ShouldBe("first01");
    }

    [Fact]
    public async Task Destinations_concurrent_writers_all_agree_on_one_code()
    {
        var stores = await CreateStoresAsync();
        var key = DestinationKey.For("alice", "https://example.com/race");

        var codes = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => stores.Destinations.AddIfAbsentAsync(key, $"code{i:D3}", Ct)));

        codes.Distinct().Count().ShouldBe(1);
    }

    // ---- Users ---------------------------------------------------------------------------

    private static User NewUser(string email) =>
        new(Guid.NewGuid().ToString("N"), "Ada", email, T0, new string('A', 64));

    [Fact]
    public async Task Users_round_trip()
    {
        var stores = await CreateStoresAsync();
        var user = NewUser("ada@example.com");

        (await stores.Users.TryCreateAsync(user, Ct)).ShouldBeTrue();

        (await stores.Users.GetAsync(user.Id, Ct)).ShouldBe(user);
    }

    [Fact]
    public async Task Users_email_is_unique()
    {
        var stores = await CreateStoresAsync();
        await stores.Users.TryCreateAsync(NewUser("ada@example.com"), Ct);
        var duplicate = NewUser("ada@example.com");

        (await stores.Users.TryCreateAsync(duplicate, Ct)).ShouldBeFalse();
        (await stores.Users.GetAsync(duplicate.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Users_concurrent_signups_with_one_email_have_exactly_one_winner()
    {
        var stores = await CreateStoresAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => stores.Users.TryCreateAsync(NewUser("race@example.com"), Ct)));

        results.Count(created => created).ShouldBe(1);
    }

    [Theory]
    [InlineData("0192f1a2b3c47d8e9f00112233445566")]
    [InlineData("bad/id")]
    public async Task Users_unknown_or_unstorable_ids_read_as_null(string id)
    {
        var stores = await CreateStoresAsync();

        (await stores.Users.GetAsync(id, Ct)).ShouldBeNull();
    }

    // ---- ID allocator --------------------------------------------------------------------

    [Fact]
    public async Task Allocator_hands_out_contiguous_blocks_starting_at_zero()
    {
        var stores = await CreateStoresAsync();

        (await stores.Allocator.AllocateAsync(100, Ct)).ShouldBe(new IdRange(0, 100));
        (await stores.Allocator.AllocateAsync(100, Ct)).ShouldBe(new IdRange(100, 200));
        (await stores.Allocator.AllocateAsync(5, Ct)).ShouldBe(new IdRange(200, 205));
    }

    [Fact]
    public async Task Allocator_concurrent_leases_never_overlap()
    {
        var stores = await CreateStoresAsync();

        var ranges = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => stores.Allocator.AllocateAsync(10, Ct)));

        var ordered = ranges.OrderBy(r => r.Start).ToArray();
        ordered[0].Start.ShouldBe(0);
        for (var i = 1; i < ordered.Length; i++)
        {
            ordered[i].Start.ShouldBe(ordered[i - 1].EndExclusive, "ranges must tile the counter with no gaps or overlaps");
        }
        ordered[^1].EndExclusive.ShouldBe(200);
    }

    // ---- Stats ---------------------------------------------------------------------------

    private static ClickAggregate Clicks(string code, DateTimeOffset at, long count) =>
        new(code, DateOnly.FromDateTime(at.UtcDateTime), count, at);

    [Fact]
    public async Task Stats_unknown_link_is_null()
    {
        var stores = await CreateStoresAsync();

        (await stores.Stats.GetAsync("missing", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Stats_accumulate_totals_and_per_day_counts()
    {
        var stores = await CreateStoresAsync();
        var day1 = T0;
        var day2 = T0.AddDays(1);

        await stores.Stats.ApplyAsync([Clicks("abc1234", day1, 3)], Ct);
        await stores.Stats.ApplyAsync([Clicks("abc1234", day1.AddHours(1), 2), Clicks("abc1234", day2, 4)], Ct);

        var stats = (await stores.Stats.GetAsync("abc1234", Ct)).ShouldNotBeNull();
        stats.TotalClicks.ShouldBe(9);
        stats.Daily[DateOnly.FromDateTime(day1.UtcDateTime)].ShouldBe(5);
        stats.Daily[DateOnly.FromDateTime(day2.UtcDateTime)].ShouldBe(4);
        stats.LastClickedAt.ShouldBe(day2);
    }

    [Fact]
    public async Task Stats_late_batches_never_move_last_clicked_backwards()
    {
        var stores = await CreateStoresAsync();

        await stores.Stats.ApplyAsync([Clicks("abc1234", T0.AddHours(5), 1)], Ct);
        await stores.Stats.ApplyAsync([Clicks("abc1234", T0, 1)], Ct); // arrives late

        var stats = (await stores.Stats.GetAsync("abc1234", Ct)).ShouldNotBeNull();
        stats.TotalClicks.ShouldBe(2);
        stats.LastClickedAt.ShouldBe(T0.AddHours(5));
    }

    [Fact]
    public async Task Stats_concurrent_consumers_lose_no_increments()
    {
        var stores = await CreateStoresAsync();

        // 20 consumers each add 5 clicks to the same (still nonexistent) link at once,
        // racing on the document's creation as well as its increments.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            stores.Stats.ApplyAsync([Clicks("hot1234", T0.AddSeconds(i), 5)], Ct)));

        var stats = (await stores.Stats.GetAsync("hot1234", Ct)).ShouldNotBeNull();
        stats.TotalClicks.ShouldBe(100);
        stats.LastClickedAt.ShouldBe(T0.AddSeconds(19));
    }

    [Fact]
    public async Task Stats_top_links_are_ordered_by_total_and_limited()
    {
        var stores = await CreateStoresAsync();
        await stores.Stats.ApplyAsync(
        [
            Clicks("low0001", T0, 1),
            Clicks("high001", T0, 50),
            Clicks("mid0001", T0, 10),
            Clicks("mid0001", T0.AddDays(1), 15), // 25 in total, over two days
        ], Ct);

        var top = await stores.Stats.GetTopAsync(2, Ct);

        top.Select(t => (t.Code, t.TotalClicks)).ShouldBe([("high001", 50L), ("mid0001", 25L)]);
    }

    [Fact]
    public async Task Stats_top_is_empty_when_nothing_was_clicked()
    {
        var stores = await CreateStoresAsync();

        (await stores.Stats.GetTopAsync(10, Ct)).ShouldBeEmpty();
    }
}
