using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using UrlShortener.Core.Links;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Tests.TestDoubles;

namespace UrlShortener.Core.Tests.Links;

public sealed class LinkServiceTests : IDisposable
{
    private const string Url = "https://example.com/articles/42?ref=home";
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeLinkRepository _repository = new();
    private readonly FakeDestinationIndex _index = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ShortCodeGenerator _generator;
    private readonly LinkService _service;

    public LinkServiceTests()
    {
        _generator = new ShortCodeGenerator(new SequentialIdAllocator(), "test-secret", blockSize: 100);
        _service = CreateService(_repository, _index, _generator);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _generator.Dispose();

    private LinkService CreateService(ILinkRepository repository, IDestinationIndex index, IShortCodeGenerator generator) =>
        new(repository, index, generator, _time, new LinkServiceSettings
        {
            BlockedHosts = new HashSet<string>(["sho.rt"], StringComparer.OrdinalIgnoreCase),
        });

    // ---- Generated codes ---------------------------------------------------------------

    [Fact]
    public async Task Creates_a_generated_link_with_metadata()
    {
        var result = await _service.CreateAsync(new(Url, null, "user-1"), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Created);
        var link = result.Link.ShouldNotBeNull();
        link.Code.Length.ShouldBe(ShortCodeGenerator.CodeLength);
        link.LongUrl.ShouldBe(Url);
        link.UserId.ShouldBe("user-1");
        link.IsCustom.ShouldBeFalse();
        link.CreatedAt.ShouldBe(Now);
        (await _repository.GetAsync(link.Code, Ct)).ShouldBe(link);
    }

    [Fact]
    public async Task Stores_the_normalised_url()
    {
        var result = await _service.CreateAsync(new("  HTTPS://Example.com/articles/42?ref=home ", null, null), Ct);

        result.Link.ShouldNotBeNull().LongUrl.ShouldBe(Url);
    }

    [Fact]
    public async Task Rejects_invalid_urls_without_touching_storage()
    {
        var result = await _service.CreateAsync(new("not a url", null, null), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Invalid);
        result.Errors.ShouldNotBeNull().ShouldContainKey("longUrl");
        _repository.Count.ShouldBe(0);
        _index.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Rejects_links_back_to_the_shortener_itself()
    {
        var result = await _service.CreateAsync(new("https://sho.rt/abc1234", null, null), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Invalid);
    }

    [Fact]
    public async Task Skips_a_generated_code_that_is_already_taken_by_a_custom_alias()
    {
        var generator = Substitute.For<IShortCodeGenerator>();
        generator.NextAsync(Arg.Any<CancellationToken>()).Returns("Taken01", "Fresh02");
        _repository.Seed(new ShortLink("Taken01", "https://other.example/", Now, null, IsCustom: true));

        var result = await CreateService(_repository, _index, generator).CreateAsync(new(Url, null, null), Ct);

        result.Link.ShouldNotBeNull().Code.ShouldBe("Fresh02");
    }

    [Fact]
    public async Task Skips_a_generated_code_that_happens_to_be_a_reserved_word()
    {
        var generator = Substitute.For<IShortCodeGenerator>();
        generator.NextAsync(Arg.Any<CancellationToken>()).Returns("openapi", "Fresh02");

        var result = await CreateService(_repository, _index, generator).CreateAsync(new(Url, null, null), Ct);

        result.Link.ShouldNotBeNull().Code.ShouldBe("Fresh02");
    }

    [Fact]
    public async Task Gives_up_after_repeated_collisions()
    {
        var generator = Substitute.For<IShortCodeGenerator>();
        generator.NextAsync(Arg.Any<CancellationToken>()).Returns("Taken01");
        _repository.Seed(new ShortLink("Taken01", "https://other.example/", Now, null, IsCustom: true));

        await Should.ThrowAsync<InvalidOperationException>(
            () => CreateService(_repository, _index, generator).CreateAsync(new(Url, null, null), Ct));
    }

    // ---- Deduplication (the book's "is longURL already in the DB?" step) ---------------

    [Fact]
    public async Task Returns_the_existing_link_when_the_same_user_shortens_the_same_url()
    {
        var first = await _service.CreateAsync(new(Url, null, "user-1"), Ct);
        _time.Advance(TimeSpan.FromDays(3));
        var second = await _service.CreateAsync(new(Url, null, "user-1"), Ct);

        second.Status.ShouldBe(CreateLinkStatus.Existing);
        second.Link.ShouldBe(first.Link);
        _repository.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Treats_differently_written_but_equivalent_urls_as_the_same_destination()
    {
        var first = await _service.CreateAsync(new("https://EXAMPLE.com/articles/42?ref=home", null, null), Ct);
        var second = await _service.CreateAsync(new("  https://example.com/articles/42?ref=home", null, null), Ct);

        second.Status.ShouldBe(CreateLinkStatus.Existing);
        second.Link.ShouldNotBeNull().Code.ShouldBe(first.Link.ShouldNotBeNull().Code);
        _repository.Count.ShouldBe(1); // found via the index, not by creating and discarding a link
    }

    [Fact]
    public async Task Never_shares_links_between_users()
    {
        var alice = await _service.CreateAsync(new(Url, null, "alice"), Ct);
        var bob = await _service.CreateAsync(new(Url, null, "bob"), Ct);
        var anonymous = await _service.CreateAsync(new(Url, null, null), Ct);

        new[] { alice, bob, anonymous }.ShouldAllBe(r => r.Status == CreateLinkStatus.Created);
        new[] { alice.Link!.Code, bob.Link!.Code, anonymous.Link!.Code }.Distinct().Count().ShouldBe(3);
        bob.Link.UserId.ShouldBe("bob");
    }

    [Fact]
    public async Task Anonymous_callers_share_one_scope()
    {
        var first = await _service.CreateAsync(new(Url, null, null), Ct);
        var second = await _service.CreateAsync(new(Url, null, null), Ct);

        second.Status.ShouldBe(CreateLinkStatus.Existing);
        second.Link.ShouldBe(first.Link);
        _repository.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Different_urls_get_different_links()
    {
        var a = await _service.CreateAsync(new(Url, null, null), Ct);
        var b = await _service.CreateAsync(new(Url + "&page=2", null, null), Ct);

        b.Status.ShouldBe(CreateLinkStatus.Created);
        b.Link.ShouldNotBeNull().Code.ShouldNotBe(a.Link.ShouldNotBeNull().Code);
    }

    [Fact]
    public async Task Creates_a_fresh_link_when_the_index_points_at_a_missing_link()
    {
        _index.Seed(DestinationKey.For(null, Url), "Ghost01");

        var result = await _service.CreateAsync(new(Url, null, null), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Created);
        result.Link.ShouldNotBeNull().Code.ShouldNotBe("Ghost01");
    }

    [Fact]
    public async Task Concurrent_identical_requests_all_receive_the_same_code()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => _service.CreateAsync(new(Url, null, "user-1"), Ct)));

        results.Select(r => r.Link!.Code).Distinct().Count().ShouldBe(1);
        results.Count(r => r.Status == CreateLinkStatus.Created).ShouldBe(1);
    }

    [Fact]
    public async Task Returns_the_race_winner_when_the_index_was_claimed_concurrently()
    {
        var winner = new ShortLink("Winner1", Url, Now, "user-1", IsCustom: false);
        _repository.Seed(winner);
        var index = Substitute.For<IDestinationIndex>();
        index.FindCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        index.AddIfAbsentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("Winner1");

        var result = await CreateService(_repository, index, _generator).CreateAsync(new(Url, null, "user-1"), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Existing);
        result.Link.ShouldBe(winner);
    }

    [Fact]
    public async Task Writes_the_link_before_indexing_it()
    {
        var repository = Substitute.For<ILinkRepository>();
        repository.TryCreateAsync(Arg.Any<ShortLink>(), Arg.Any<CancellationToken>()).Returns(true);
        var index = Substitute.For<IDestinationIndex>();
        index.FindCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        index.AddIfAbsentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(1));

        var result = await CreateService(repository, index, _generator).CreateAsync(new(Url, null, "user-1"), Ct);
        var code = result.Link.ShouldNotBeNull().Code;

        Received.InOrder(() =>
        {
            index.FindCodeAsync(DestinationKey.For("user-1", Url), Arg.Any<CancellationToken>());
            repository.TryCreateAsync(Arg.Is<ShortLink>(l => l.Code == code), Arg.Any<CancellationToken>());
            index.AddIfAbsentAsync(DestinationKey.For("user-1", Url), code, Arg.Any<CancellationToken>());
        });
    }

    // ---- Custom aliases ----------------------------------------------------------------

    [Fact]
    public async Task Creates_a_custom_alias()
    {
        var result = await _service.CreateAsync(new(Url, "  launch-day ", "user-1"), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Created);
        var link = result.Link.ShouldNotBeNull();
        link.Code.ShouldBe("launch-day");
        link.IsCustom.ShouldBeTrue();
    }

    [Fact]
    public async Task Custom_aliases_bypass_deduplication_and_are_not_indexed()
    {
        var generated = await _service.CreateAsync(new(Url, null, "user-1"), Ct);
        var custom = await _service.CreateAsync(new(Url, "launch-day", "user-1"), Ct);
        var generatedAgain = await _service.CreateAsync(new(Url, null, "user-1"), Ct);

        custom.Status.ShouldBe(CreateLinkStatus.Created);
        _index.Count.ShouldBe(1);
        generatedAgain.Link.ShouldBe(generated.Link); // dedup still resolves to the generated one
        _repository.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Reports_a_taken_alias()
    {
        await _service.CreateAsync(new(Url, "launch-day", "user-1"), Ct);

        var result = await _service.CreateAsync(new("https://another.example/", "launch-day", "user-2"), Ct);

        result.Status.ShouldBe(CreateLinkStatus.AliasTaken);
        result.Errors.ShouldNotBeNull().ShouldContainKey("customAlias");
        (await _repository.GetAsync("launch-day", Ct)).ShouldNotBeNull().UserId.ShouldBe("user-1");
    }

    [Fact]
    public async Task Aliases_are_case_sensitive()
    {
        await _service.CreateAsync(new(Url, "Launch", null), Ct);

        var result = await _service.CreateAsync(new(Url, "launch", null), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Created);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("api")]
    [InlineData("bad alias")]
    public async Task Rejects_invalid_aliases(string alias)
    {
        var result = await _service.CreateAsync(new(Url, alias, null), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Invalid);
        result.Errors.ShouldNotBeNull().ShouldContainKey("customAlias");
        _repository.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Exactly_one_of_many_concurrent_claims_on_an_alias_wins()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => _service.CreateAsync(new($"https://example.com/{i}", "hot-alias", $"user-{i}"), Ct)));

        results.Count(r => r.Status == CreateLinkStatus.Created).ShouldBe(1);
        results.Count(r => r.Status == CreateLinkStatus.AliasTaken).ShouldBe(19);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_alias_means_generate_a_code(string? alias)
    {
        var result = await _service.CreateAsync(new(Url, alias, null), Ct);

        result.Link.ShouldNotBeNull().IsCustom.ShouldBeFalse();
    }
}
