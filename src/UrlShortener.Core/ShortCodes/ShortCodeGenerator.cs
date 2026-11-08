namespace UrlShortener.Core.ShortCodes;

/// <summary>A contiguous block of counter values, <c>[Start, EndExclusive)</c>, leased by one app instance.</summary>
public readonly record struct IdRange(long Start, long EndExclusive)
{
    public long Count => EndExclusive - Start;
}

/// <summary>
/// Hands out non-overlapping blocks of a global counter (the "key generation service").
/// Each app instance leases a block and then generates codes from memory, so the shared
/// counter is touched once per block, not once per link.
/// </summary>
public interface IIdRangeAllocator
{
    Task<IdRange> AllocateAsync(int size, CancellationToken cancellationToken);
}

public interface IShortCodeGenerator
{
    ValueTask<string> NextAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Generates fixed-length, collision-free short codes:
/// <c>counter (leased in blocks) → Feistel permutation → base62</c>.
/// </summary>
public sealed class ShortCodeGenerator : IShortCodeGenerator, IDisposable
{
    /// <summary>
    /// 62^7 ≈ 3.5 trillion codes, about 29 times the 120 billion links that 100 years at
    /// 100M links per month would need. Six characters (56.8 billion) would not be enough.
    /// </summary>
    public const int CodeLength = 7;

    private readonly IIdRangeAllocator _allocator;
    private readonly FeistelPermutation _permutation;
    private readonly int _blockSize;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _next;
    private long _end;

    public ShortCodeGenerator(IIdRangeAllocator allocator, string secret, int blockSize = 1_000)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        _allocator = allocator;
        _blockSize = blockSize;
        _permutation = new FeistelPermutation(Base62.KeySpace(CodeLength), secret);
    }

    public async ValueTask<string> NextAsync(CancellationToken cancellationToken)
    {
        long id;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_next >= _end)
            {
                var range = await _allocator.AllocateAsync(_blockSize, cancellationToken).ConfigureAwait(false);
                if (range.Count <= 0)
                {
                    throw new InvalidOperationException("The ID allocator returned an empty range.");
                }
                (_next, _end) = (range.Start, range.EndExclusive);
            }

            id = _next++;
        }
        finally
        {
            _gate.Release();
        }

        if (id < 0 || (ulong)id >= _permutation.DomainSize)
        {
            throw new InvalidOperationException("The short-code key space is exhausted.");
        }

        return Base62.Encode(_permutation.Permute((ulong)id), CodeLength);
    }

    public void Dispose() => _gate.Dispose();
}
