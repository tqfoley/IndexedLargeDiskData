namespace IndexedLargeDiskData.Caching;

/// <summary>
/// A borrowed view over one cached block. The block cannot be evicted while the lease is alive.
/// </summary>
/// <remarks>
/// Always consume with <c>using</c>. Never let the returned span outlive the lease: the memory is
/// native and is handed to another block as soon as the last lease is released and CLOCK reclaims it.
/// </remarks>
public readonly ref struct BlockLease
{
    private readonly CacheBlock? _block;

    internal BlockLease(CacheBlock? block, int length)
    {
        _block = block;
        Length = length;
    }

    /// <summary>Gets the number of valid bytes in the block, which is short at end of file.</summary>
    public int Length { get; }

    /// <summary>Gets a read-only view over the block contents.</summary>
    public unsafe ReadOnlySpan<byte> Span
    {
        get
        {
            if (_block is null)
            {
                return default;
            }
            else
            {
                return new ReadOnlySpan<byte>((void*)_block.Pointer, Length);
            }
        }
    }

    /// <summary>Releases the lease, allowing the block to be evicted again.</summary>
    public void Dispose() => _block?.Release();
}

/// <summary>One resident block: a pointer into a native slab plus its cache bookkeeping.</summary>
internal sealed class CacheBlock
{
    internal const int StateLoading = 0;
    internal const int StateReady = 1;
    internal const int StateEvicted = 2;

    private int _state = StateLoading;
    private int _refCount;
    private int _accessed;

    internal CacheBlock(long key, int initialRefCount = 0)
    {
        Key = key;
        _refCount = initialRefCount;
    }

    internal long Key { get; }

    internal nint Pointer { get; set; }

    internal int Length { get; set; }

    /// <summary>Gets or sets the slot this block occupies in the CLOCK ring, or -1 when unplaced.</summary>
    internal int Slot { get; set; } = -1;

    internal int State => Volatile.Read(ref _state);

    internal void MarkReady() => Volatile.Write(ref _state, StateReady);

    internal void MarkEvicted() => Volatile.Write(ref _state, StateEvicted);

    internal void Touch() => Volatile.Write(ref _accessed, 1);

    /// <summary>Clears the CLOCK reference bit and reports whether it had been set.</summary>
    internal bool ClearAccessed() => Interlocked.Exchange(ref _accessed, 0) != 0;

    /// <summary>Takes a reference, returning false if the block was evicted from under the caller.</summary>
    internal bool TryAcquire()
    {
        // A hugely negative count means an evictor has already claimed the block exclusively.
        if (Interlocked.Increment(ref _refCount) <= 0 || State == StateEvicted)
        {
            Interlocked.Decrement(ref _refCount);
            return false;
        }

        Touch();
        return true;
    }

    internal void Release() => Interlocked.Decrement(ref _refCount);

    /// <summary>Claims the block for eviction, succeeding only when nobody holds a lease.</summary>
    internal bool TryClaimForEviction() =>
        Interlocked.CompareExchange(ref _refCount, int.MinValue / 2, 0) == 0;
}
