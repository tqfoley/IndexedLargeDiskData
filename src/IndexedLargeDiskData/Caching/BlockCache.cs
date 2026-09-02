using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IndexedLargeDiskData.Caching;

/// <summary>
/// A fixed-block, byte-budgeted cache held in native memory and shared by every store.
/// </summary>
/// <remarks>
/// <para>
/// The budget is committed as native slabs at construction rather than allocated on the managed heap.
/// A 20 GiB managed cache would sit in gen2 and the large object heap, where it would dominate every
/// collection; native slabs are invisible to the GC and never move.
/// </para>
/// <para>
/// Eviction is CLOCK: each block carries a reference bit that a scan clears before reclaiming the
/// block on its second visit. Blocks are reference counted, so a block that some reader currently
/// holds a <see cref="BlockLease"/> on is skipped rather than freed underneath them.
/// </para>
/// </remarks>
public sealed class BlockCache : IDisposable
{
    private const int FileIdShift = 40;
    private const ulong BlockIndexMask = (1L << FileIdShift) - 1;
    private const int MaxFileId = (1 << (63 - FileIdShift)) - 1;
    private const ulong SlabBytes = 256L * 1024 * 1024;

    private readonly ConcurrentDictionary<ulong, CacheBlock> _blocks = new();
    private readonly ConcurrentDictionary<int, CachedFile> _files = new();
    private readonly ConcurrentStack<int> _reusableFileIds = new();
    private readonly CacheBlock?[] _clock;
    private readonly nint[] _slotPointers;
    private readonly int[] _freeSlots;
    private readonly List<nint> _slabs = [];
    private readonly Lock _evictLock = new();

    private int _freeSlotCount;
    private int _hand;
    private int _nextFileId;
    private ulong _hits;
    private ulong _misses;
    private ulong _evictions;
    private bool _disposed;

    /// <summary>Creates a cache that holds <paramref name="budgetBytes"/> of blocks.</summary>
    /// <param name="blockSize">Block size in bytes. Must be a power of two of at least 512.</param>
    /// <param name="budgetBytes">Total native memory to commit, rounded down to a whole block count.</param>
    public BlockCache(int blockSize, ulong budgetBytes)
    {
        if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentException("Block size must be a power of two of at least 512.", nameof(blockSize));
        }

        ulong capacity = budgetBytes / (ulong)blockSize;
        if (capacity < 16)
        {
            throw new ArgumentException("Budget must hold at least 16 blocks.", nameof(budgetBytes));
        }

        if (capacity > int.MaxValue)
        {
            throw new ArgumentException("Budget exceeds the maximum addressable block count.", nameof(budgetBytes));
        }

        BlockSize = blockSize;
        Capacity = (int)capacity;
        _clock = new CacheBlock?[Capacity];
        _slotPointers = new nint[Capacity];
        _freeSlots = new int[Capacity];

        AllocateSlabs();

        for (int i = 0; i < Capacity; i++)
        {
            _freeSlots[i] = Capacity - 1 - i;
        }

        _freeSlotCount = Capacity;
    }

    /// <summary>Creates a cache configured from <paramref name="options"/>.</summary>
    public BlockCache(StoreOptions options)
        : this(options.BlockSize, options.CacheBudgetBytes)
    {
    }

    /// <summary>Gets the block size in bytes.</summary>
    public int BlockSize { get; }

    /// <summary>Gets the number of blocks the cache can hold.</summary>
    public int Capacity { get; }

    /// <summary>Gets the number of blocks currently resident.</summary>
    public int Resident => _blocks.Count;

    /// <summary>Gets the number of leases served from memory.</summary>
    public ulong Hits => Interlocked.Read(ref _hits);

    /// <summary>Gets the number of leases that had to read from disk.</summary>
    public ulong Misses => Interlocked.Read(ref _misses);

    /// <summary>Gets the number of blocks reclaimed by CLOCK.</summary>
    public ulong Evictions => Interlocked.Read(ref _evictions);

    /// <summary>Registers an open file handle with the cache, which takes ownership of it.</summary>
    /// <param name="handle">An open handle. Disposed when the returned <see cref="CachedFile"/> is disposed.</param>
    /// <param name="path">The path the handle refers to, for diagnostics.</param>
    public CachedFile Register(SafeFileHandle handle, string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_reusableFileIds.TryPop(out int id))
        {
            id = Interlocked.Increment(ref _nextFileId);
            if (id > MaxFileId)
            {
                throw new InvalidOperationException("Too many files registered with the cache.");
            }
        }

        CachedFile file = new(this, id, handle, path);
        _files[id] = file;
        return file;
    }

    /// <summary>Acquires a lease on one block of <paramref name="file"/>, reading it if necessary.</summary>
    public BlockLease Acquire(CachedFile file, ulong blockIndex)
    {
        ArgumentNullException.ThrowIfNull(file);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if ((ulong)blockIndex > BlockIndexMask)
        {
            throw new ArgumentOutOfRangeException(nameof(blockIndex));
        }

        ulong key = MakeKey(file.Id, blockIndex);

        while (true)
        {
            if (_blocks.TryGetValue(key, out CacheBlock? existing))
            {
                if (existing.State == CacheBlock.StateLoading)
                {
                    // The loading thread holds the block's monitor for the duration of the read.
                    lock (existing)
                    {
                    }
                }

                if (existing.TryAcquire())
                {
                    Interlocked.Increment(ref _hits);
                    return new BlockLease(existing, existing.Length);
                }

                continue;
            }

            CacheBlock fresh = new(key, initialRefCount: 1);
            lock (fresh)
            {
                if (!_blocks.TryAdd(key, fresh))
                {
                    continue;
                }

                try
                {
                    int slot = AcquireSlot();
                    fresh.Slot = slot;
                    fresh.Pointer = _slotPointers[slot];
                    fresh.Length = ReadBlock(file, blockIndex, fresh.Pointer);

                    lock (_evictLock)
                    {
                        _clock[slot] = fresh;
                    }

                    fresh.MarkReady();
                }
                catch
                {
                    _blocks.TryRemove(new KeyValuePair<ulong, CacheBlock>(key, fresh));
                    if (fresh.Slot >= 0)
                    {
                        ReleaseSlot(fresh.Slot);
                        fresh.Slot = -1;
                    }

                    fresh.MarkEvicted();
                    throw;
                }
            }

            Interlocked.Increment(ref _misses);
            return new BlockLease(fresh, fresh.Length);
        }
    }

    /// <summary>Drops the cached copy of one block, if it is resident and unpinned.</summary>
    public void Invalidate(int fileId, ulong blockIndex)
    {
        ulong key = MakeKey(fileId, blockIndex);
        if (_blocks.TryGetValue(key, out CacheBlock? block))
        {
            Discard(key, block);
        }
    }

    /// <summary>Drops every cached block belonging to a file.</summary>
    public void InvalidateFile(int fileId)
    {
        ulong low = MakeKey(fileId, 0);
        ulong high = low + BlockIndexMask;

        foreach (KeyValuePair<ulong, CacheBlock> pair in _blocks)
        {
            if (pair.Key >= low && pair.Key <= high)
            {
                Discard(pair.Key, pair.Value);
            }
        }
    }

    internal void Unregister(CachedFile file)
    {
        InvalidateFile(file.Id);
        if (_files.TryRemove(file.Id, out _))
        {
            _reusableFileIds.Push(file.Id);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (CachedFile file in _files.Values)
        {
            file.Dispose();
        }

        _files.Clear();
        _blocks.Clear();

        lock (_evictLock)
        {
            foreach (nint slab in _slabs)
            {
                unsafe
                {
                    NativeMemory.AlignedFree((void*)slab);
                }
            }

            _slabs.Clear();
        }
    }

    private static ulong MakeKey(int fileId, ulong blockIndex) => ((ulong)fileId << FileIdShift) | blockIndex;

    private void Discard(ulong key, CacheBlock block)
    {
        if (!_blocks.TryRemove(new KeyValuePair<ulong, CacheBlock>(key, block)))
        {
            return;
        }

        // If a reader still holds the block we simply orphan it: it is unreachable through the
        // dictionary now, and the CLOCK scan reclaims its slot once the last lease is released.
        if (block.TryClaimForEviction())
        {
            block.MarkEvicted();
            lock (_evictLock)
            {
                if (block.Slot >= 0 && _clock[block.Slot] == block)
                {
                    _clock[block.Slot] = null;
                    ReleaseSlotLocked(block.Slot);
                    block.Slot = -1;
                }
            }
        }
    }

    private unsafe void AllocateSlabs()
    {
        ulong remaining = (ulong)Capacity * (ulong)BlockSize;
        int slot = 0;

        while (remaining > 0)
        {
            ulong take = Math.Min(SlabBytes - (SlabBytes % (ulong)BlockSize), remaining);
            void* slab = NativeMemory.AlignedAlloc((nuint)take, (nuint)BlockSize);
            if (slab is null)
            {
                throw new OutOfMemoryException("Unable to commit the block cache budget.");
            }

            _slabs.Add((nint)slab);

            for (ulong offset = 0; offset + (ulong)BlockSize <= take; offset += (ulong)BlockSize)
            {
                _slotPointers[slot++] = (nint)((byte*)slab + offset);
            }

            remaining -= take;
        }
    }

    private int AcquireSlot()
    {
        lock (_evictLock)
        {
            if (_freeSlotCount > 0)
            {
                return _freeSlots[--_freeSlotCount];
            }

            // Two full sweeps: the first clears reference bits, the second reclaims.
            ulong limit = 2UL * (ulong)Capacity;
            for (ulong scanned = 0; scanned < limit; scanned++)
            {
                int slot = _hand;
                if (_hand + 1 == Capacity)
                {
                    _hand = 0;
                }
                else
                {
                    _hand++;
                }

                CacheBlock? candidate = _clock[slot];
                if (candidate is null)
                {
                    return slot;
                }

                if (candidate.State != CacheBlock.StateReady || candidate.ClearAccessed())
                {
                    continue;
                }

                if (!candidate.TryClaimForEviction())
                {
                    continue;
                }

                candidate.MarkEvicted();
                _blocks.TryRemove(new KeyValuePair<ulong, CacheBlock>(candidate.Key, candidate));
                _clock[slot] = null;
                candidate.Slot = -1;
                Interlocked.Increment(ref _evictions);
                return slot;
            }

            throw new InvalidOperationException(
                "Block cache is exhausted: every block is pinned by a live lease. " +
                "This means leases are being held open; check for a missing 'using'.");
        }
    }

    private void ReleaseSlot(int slot)
    {
        lock (_evictLock)
        {
            _clock[slot] = null;
            ReleaseSlotLocked(slot);
        }
    }

    private void ReleaseSlotLocked(int slot)
    {
        if (_freeSlotCount < _freeSlots.Length)
        {
            _freeSlots[_freeSlotCount++] = slot;
        }
    }

    private unsafe int ReadBlock(CachedFile file, ulong blockIndex, nint destination)
    {
        ulong offset = blockIndex * (ulong)BlockSize;
        ulong length = file.Length;
        if (offset >= length)
        {
            return 0;
        }

        int wanted = (int)Math.Min((ulong)BlockSize, length - offset);
        Span<byte> target = new((void*)destination, wanted);

        int total = 0;
        while (total < wanted)
        {
            int read = RandomAccess.Read(file.Handle, target[total..], (long)(offset + (ulong)total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
