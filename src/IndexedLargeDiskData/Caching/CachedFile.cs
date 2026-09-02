using Microsoft.Win32.SafeHandles;

namespace IndexedLargeDiskData.Caching;

/// <summary>A file registered with a <see cref="BlockCache"/>, identified by a small integer.</summary>
/// <remarks>
/// The integer id is packed with the block index into a single 64-bit cache key, which keeps the
/// cache dictionary free of composite-key allocations and comparers.
/// </remarks>
public sealed class CachedFile : IDisposable
{
    private readonly BlockCache _cache;
    private ulong _length;
    private int _disposed;

    internal CachedFile(BlockCache cache, int id, SafeFileHandle handle, string path)
    {
        _cache = cache;
        Id = id;
        Handle = handle;
        Path = path;
        _length = (ulong)RandomAccess.GetLength(handle);
    }

    /// <summary>Gets the cache-local identifier for this file.</summary>
    public int Id { get; }

    /// <summary>Gets the full path of the file.</summary>
    public string Path { get; }

    /// <summary>Gets the length of the file in bytes as last observed by the cache.</summary>
    public ulong Length => Interlocked.Read(ref _length);

    internal SafeFileHandle Handle { get; }

    /// <summary>Records that the file has grown, so cached blocks can be re-read at their new length.</summary>
    /// <param name="length">The new file length in bytes.</param>
    public void SetLength(ulong length) => Interlocked.Exchange(ref _length, length);

    /// <summary>Drops every cached block belonging to this file.</summary>
    public void InvalidateAll() => _cache.InvalidateFile(Id);

    /// <summary>Drops the cached copy of a single block.</summary>
    /// <param name="blockIndex">The zero-based block index within this file.</param>
    public void Invalidate(ulong blockIndex) => _cache.Invalidate(Id, blockIndex);

    /// <summary>Acquires a pinned lease on one block, reading it from disk if it is not cached.</summary>
    /// <param name="blockIndex">The zero-based block index within this file.</param>
    public BlockLease Acquire(ulong blockIndex) => _cache.Acquire(this, blockIndex);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cache.Unregister(this);
        Handle.Dispose();
    }
}
