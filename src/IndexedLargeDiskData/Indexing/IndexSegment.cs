using System.Buffers.Binary;
using IndexedLargeDiskData.Caching;
using Microsoft.Win32.SafeHandles;

namespace IndexedLargeDiskData.Indexing;

/// <summary>
/// One immutable sorted run of index entries, plus the fences and Bloom filter that make it searchable.
/// </summary>
/// <remarks>
/// <para>
/// Segments are written once and never modified, which is the whole reason this design suits
/// append-only data: there are no tombstones, no in-place updates and no version chains. A merge only
/// ever concatenates sorted runs.
/// </para>
/// <para>
/// File layout: a 128-byte header, then the entries sorted by key, then one fence key per
/// <see cref="FenceStride"/> entries, then the Bloom filter. Fences are the only part loaded eagerly
/// into managed memory; everything else is read on demand through the shared block cache.
/// </para>
/// </remarks>
internal sealed class IndexSegment : IDisposable
{
    internal const long Magic = 0x314745534458_4449L;
    internal const int Version = 1;
    internal const int HeaderSize = 128;

    private readonly CachedFile _file;
    private readonly int _blockSize;
    private readonly long[] _fences;
    private bool _disposed;

    private IndexSegment(int id, string path, CachedFile file, int blockSize, in SegmentHeader header, long[] fences)
    {
        Id = id;
        Path = path;
        _file = file;
        _blockSize = blockSize;
        _fences = fences;
        Header = header;
    }

    /// <summary>Gets the numeric id encoded in the file name.</summary>
    internal int Id { get; }

    /// <summary>Gets the full path of the segment file.</summary>
    internal string Path { get; }

    internal SegmentHeader Header { get; }

    /// <summary>Gets the number of entries in the segment.</summary>
    internal long EntryCount => Header.EntryCount;

    /// <summary>Gets the merge level this segment sits at.</summary>
    internal int Level => Header.Level;

    /// <summary>Gets the number of entries between successive fence keys.</summary>
    internal int FenceStride => Header.FenceStride;

    /// <summary>Gets the exclusive record ordinal up to which this segment is complete.</summary>
    internal long CoveredUpTo => Header.CoveredUpTo;

    /// <summary>Opens an existing segment file and loads its fences.</summary>
    internal static IndexSegment Open(string path, BlockCache cache)
    {
        int id = ParseId(path);

        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CachedFile file;
        try
        {
            file = cache.Register(handle, path);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        try
        {
            Span<byte> raw = stackalloc byte[HeaderSize];
            ReadExact(handle, 0, raw);
            SegmentHeader header = SegmentHeader.Read(raw, path);

            long[] fences = new long[header.FenceCount];
            if (header.FenceCount > 0)
            {
                byte[] buffer = new byte[header.FenceCount * sizeof(long)];
                ReadExact(handle, header.FenceOffset, buffer);
                for (int i = 0; i < fences.Length; i++)
                {
                    fences[i] = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i * sizeof(long)));
                }
            }

            return new IndexSegment(id, path, file, cache.BlockSize, header, fences);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Extracts the numeric id from a segment file name.</summary>
    internal static int ParseId(string path) =>
        int.Parse(System.IO.Path.GetFileNameWithoutExtension(path));

    /// <summary>Appends every ordinal stored under <paramref name="key"/> to <paramref name="results"/>.</summary>
    /// <returns>The number of ordinals appended.</returns>
    internal int Lookup(long key, List<long> results)
    {
        if (EntryCount == 0 || key < Header.MinKey || key > Header.MaxKey)
        {
            return 0;
        }

        if (Header.BloomBlockCount > 0 && !BloomMayContain(key))
        {
            return 0;
        }

        (long lo, long hi) = FenceRange(key);
        long index = LowerBound(key, lo, hi);

        int found = 0;
        while (index < hi)
        {
            IndexEntry entry = ReadEntry(index);
            if (entry.Key != key)
            {
                break;
            }

            results.Add(entry.Ordinal);
            found++;
            index++;
        }

        return found;
    }

    /// <summary>Streams every entry in key order, bypassing the cache so a merge does not evict hot blocks.</summary>
    internal IEnumerable<IndexEntry> EnumerateEntries(int bufferBytes = 1 << 20)
    {
        int perBuffer = Math.Max(bufferBytes / IndexEntry.Size, 1);
        byte[] buffer = new byte[perBuffer * IndexEntry.Size];

        long remaining = EntryCount;
        long offset = Header.EntriesOffset;

        while (remaining > 0)
        {
            int take = (int)Math.Min(perBuffer, remaining);
            int bytes = take * IndexEntry.Size;
            ReadExact(_file.Handle, offset, buffer, bytes);

            for (int i = 0; i < take; i++)
            {
                yield return EntryAt(buffer, i);
            }

            offset += bytes;
            remaining -= take;
        }
    }

    private static IndexEntry EntryAt(byte[] buffer, int index) =>
        IndexEntry.Read(buffer.AsSpan(index * IndexEntry.Size));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _file.Dispose();
    }

    internal static void ReadExact(SafeFileHandle handle, long offset, byte[] buffer, int count) =>
        ReadExact(handle, offset, buffer.AsSpan(0, count));

    internal static void ReadExact(SafeFileHandle handle, long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read == 0)
            {
                throw new EndOfStreamException("Index segment is truncated.");
            }

            total += read;
        }
    }

    private bool BloomMayContain(long key)
    {
        int block = BlockedBloom.BlockOf(key, Header.BloomBlockCount);
        Span<byte> raw = stackalloc byte[BlockedBloom.BlockBytes];
        ReadThroughCache(Header.BloomOffset + BlockedBloom.OffsetOf(block), raw);
        return BlockedBloom.MayContain(raw, key, Header.BloomBlockCount);
    }

    private (long Low, long High) FenceRange(long key)
    {
        if (_fences.Length == 0)
        {
            return (0, EntryCount);
        }

        int lower = LowerBoundFence(key, inclusive: true);
        int upper = LowerBoundFence(key, inclusive: false);

        long low;
        if (lower == 0)
        {
            low = 0;
        }
        else
        {
            low = (long)(lower - 1) * FenceStride;
        }

        long high;
        if (upper >= _fences.Length)
        {
            high = EntryCount;
        }
        else
        {
            high = Math.Min(EntryCount, (long)upper * FenceStride);
        }
        return (low, Math.Max(high, low));
    }

    /// <summary>Finds the first fence at or after <paramref name="key"/>, or strictly after it.</summary>
    private int LowerBoundFence(long key, bool inclusive)
    {
        int lo = 0;
        int hi = _fences.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            bool before;
            if (inclusive)
            {
                before = _fences[mid] < key;
            }
            else
            {
                before = _fences[mid] <= key;
            }
            if (before)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private long LowerBound(long key, long lo, long hi)
    {
        while (lo < hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            if (ReadEntry(mid).Key < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private IndexEntry ReadEntry(long index)
    {
        Span<byte> raw = stackalloc byte[IndexEntry.Size];
        ReadThroughCache(Header.EntriesOffset + (index * IndexEntry.Size), raw);
        return IndexEntry.Read(raw);
    }

    private void ReadThroughCache(long offset, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            long blockIndex = offset / _blockSize;
            int inBlock = (int)(offset % _blockSize);

            using BlockLease lease = _file.Acquire(blockIndex);
            int available = lease.Length - inBlock;
            if (available <= 0)
            {
                throw new InvalidDataException($"Index segment '{Path}' is shorter than its header claims.");
            }

            int take = Math.Min(destination.Length, available);
            lease.Span.Slice(inBlock, take).CopyTo(destination);
            destination = destination[take..];
            offset += take;
        }
    }
}
