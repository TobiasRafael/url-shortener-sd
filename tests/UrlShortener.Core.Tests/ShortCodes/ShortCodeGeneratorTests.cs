using NSubstitute;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Tests.TestDoubles;

namespace UrlShortener.Core.Tests.ShortCodes;

public sealed class ShortCodeGeneratorTests
{
    private const string Secret = "test-secret";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generates_seven_character_base62_codes()
    {
        using var generator = new ShortCodeGenerator(new SequentialIdAllocator(), Secret);

        var code = await generator.NextAsync(Ct);

        code.Length.ShouldBe(ShortCodeGenerator.CodeLength);
        Base62.IsBase62(code).ShouldBeTrue();
    }

    [Fact]
    public async Task Code_decodes_back_to_the_counter_value_it_came_from()
    {
        using var generator = new ShortCodeGenerator(new SequentialIdAllocator(start: 41), Secret);
        var permutation = new FeistelPermutation(Base62.KeySpace(7), Secret);

        var code = await generator.NextAsync(Ct);

        permutation.Inverse(Base62.Decode(code)).ShouldBe(41UL);
    }

    [Fact]
    public async Task Leases_a_new_block_only_when_the_current_one_is_used_up()
    {
        var allocator = new SequentialIdAllocator();
        using var generator = new ShortCodeGenerator(allocator, Secret, blockSize: 10);

        for (var i = 0; i < 25; i++)
        {
            await generator.NextAsync(Ct);
        }

        allocator.Calls.ShouldBe(3); // 10 + 10 + 5
    }

    [Fact]
    public async Task Concurrent_callers_never_receive_the_same_code()
    {
        var allocator = new SequentialIdAllocator();
        using var generator = new ShortCodeGenerator(allocator, Secret, blockSize: 50);

        var codes = await Task.WhenAll(Enumerable.Range(0, 5_000).Select(_ => generator.NextAsync(Ct).AsTask()));

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(5_000);
        allocator.Calls.ShouldBe(100);
    }

    [Fact]
    public async Task Two_instances_sharing_one_allocator_never_collide()
    {
        // This mirrors production: several API replicas lease from the same counter.
        var allocator = new SequentialIdAllocator();
        using var replicaA = new ShortCodeGenerator(allocator, Secret, blockSize: 7);
        using var replicaB = new ShortCodeGenerator(allocator, Secret, blockSize: 7);

        var codes = await Task.WhenAll(Enumerable.Range(0, 1_000)
            .Select(i => (i % 2 == 0 ? replicaA : replicaB).NextAsync(Ct).AsTask()));

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(1_000);
    }

    [Fact]
    public async Task Throws_when_the_allocator_returns_an_empty_range()
    {
        var allocator = Substitute.For<IIdRangeAllocator>();
        allocator.AllocateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new IdRange(5, 5));
        using var generator = new ShortCodeGenerator(allocator, Secret);

        await Should.ThrowAsync<InvalidOperationException>(() => generator.NextAsync(Ct).AsTask());
    }

    [Fact]
    public async Task Throws_once_the_key_space_is_exhausted()
    {
        var lastId = (long)Base62.KeySpace(7) - 1;
        using var generator = new ShortCodeGenerator(new SequentialIdAllocator(start: lastId), Secret, blockSize: 2);

        (await generator.NextAsync(Ct)).Length.ShouldBe(7); // the very last valid ID
        await Should.ThrowAsync<InvalidOperationException>(() => generator.NextAsync(Ct).AsTask());
    }

    [Fact]
    public async Task Honours_cancellation_while_waiting_for_a_block()
    {
        var allocator = Substitute.For<IIdRangeAllocator>();
        allocator.AllocateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new IdRange(0, 1);
            });
        using var generator = new ShortCodeGenerator(allocator, Secret);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => generator.NextAsync(cts.Token).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_block_sizes(int blockSize) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new ShortCodeGenerator(new SequentialIdAllocator(), Secret, blockSize));
}
