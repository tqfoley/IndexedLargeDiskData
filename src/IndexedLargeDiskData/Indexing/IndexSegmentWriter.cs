using System.Buffers.Binary;

namespace IndexedLargeDiskData.Indexing;

/// <summary>Writes one immutable index segment from a stream of entries already in sort order.</summary>
/// <remarks>
/// The entry count has to be known up front so the header can carry the section offsets without a
/// second pass over the file. Both callers know it: a flush knows its memtable size, and a merge
/// knows the sum of its inputs.
/// </remarks>
internal static class IndexSegmentWriter
{
    /// <summary>Entries written between progress reports.</summary>
    private const long ProgressInterval = 1 << 16;

    /// <summary>Writes a segment and returns its header.</summary>
    /// <param name="path">Destination path. Overwritten if it exists.</param>
    /// <param name="entries">Entries in ascending (key, ordinal) order. Exactly <paramref name="entryCount"/> of them.</param>
    /// <param name="entryCount">The number of entries that <paramref name="entries"/> will yield.</param>
    /// <param name="level">The merge level to stamp on the segment.</param>
    /// <param name="coveredUpTo">The exclusive record ordinal the segment is complete up to.</param>
    /// <param name="options">Fence stride and Bloom sizing.</param>
    /// <param name="onProgress">
    /// Optional sink called with the number of entries written since the last call, roughly every
    /// <see cref="ProgressInterval"/> entries. Reporting per entry would cost more than the write.
    /// </param>
    internal static SegmentHeader Write(
        string path,
        IEnumerable<IndexEntry> entries,
        long entryCount,
        int level,
        long coveredUpTo,
        StoreOptions options,
        Action<long>? onProgress = null)
    {
        long sinceReport = 0;
        long fenceCount;
        if (entryCount == 0)
        {
            fenceCount = 0;
        }
        else
        {
            fenceCount = (entryCount + options.FenceStride - 1) / options.FenceStride;
        }
        long[] fences = new long[fenceCount];
        int bloomBlocks = BlockedBloom.BlockCount(entryCount, options.BloomBitsPerKey);
        byte[] bloom = new byte[(long)bloomBlocks * BlockedBloom.BlockBytes];

        long written = 0;
        long minKey = long.MaxValue;
        long maxKey = long.MinValue;
        long previous = long.MinValue;
        bool first = true;

        // Reserve the header; it is rewritten once the key range is known.
        using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            stream.Seek(IndexSegment.HeaderSize, SeekOrigin.Begin);

            byte[] buffer = new byte[1 << 20];
            int used = 0;

            foreach (IndexEntry entry in entries)
            {
                if (!first && entry.Key < previous)
                {
                    throw new InvalidOperationException("Entries were not supplied in ascending key order.");
                }

                if (written >= entryCount)
                {
                    throw new InvalidOperationException("More entries were supplied than declared.");
                }

                if (written % options.FenceStride == 0)
                {
                    fences[written / options.FenceStride] = entry.Key;
                }

                BlockedBloom.Add(bloom, bloomBlocks, entry.Key);

                if (used + IndexEntry.Size > buffer.Length)
                {
                    stream.Write(buffer, 0, used);
                    used = 0;
                }

                IndexEntry.Write(buffer.AsSpan(used), entry);
                used += IndexEntry.Size;

                minKey = Math.Min(minKey, entry.Key);
                maxKey = Math.Max(maxKey, entry.Key);
                previous = entry.Key;
                first = false;
                written++;

                if (onProgress is not null && ++sinceReport >= ProgressInterval)
                {
                    onProgress(sinceReport);
                    sinceReport = 0;
                }
            }

            if (onProgress is not null && sinceReport > 0)
            {
                onProgress(sinceReport);
            }

            if (used > 0)
            {
                stream.Write(buffer, 0, used);
            }

            if (written != entryCount)
            {
                throw new InvalidOperationException(
                    $"Declared {entryCount} entries but only {written} were supplied.");
            }

            if (entryCount == 0)
            {
                minKey = 0;
                maxKey = 0;
            }

            SegmentHeader header = SegmentHeader.Create(
                entryCount, minKey, maxKey, coveredUpTo, level, options.FenceStride, options.BloomBitsPerKey);

            WritePadded(stream, header.FenceOffset);
            if (fences.Length > 0)
            {
                byte[] fenceBytes = new byte[fences.Length * sizeof(long)];
                for (int i = 0; i < fences.Length; i++)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(fenceBytes.AsSpan(i * sizeof(long)), fences[i]);
                }

                stream.Write(fenceBytes, 0, fenceBytes.Length);
            }

            WritePadded(stream, header.BloomOffset);
            stream.Write(bloom, 0, bloom.Length);

            stream.Seek(0, SeekOrigin.Begin);
            Span<byte> headerBytes = stackalloc byte[IndexSegment.HeaderSize];
            header.Write(headerBytes);
            stream.Write(headerBytes);

            stream.Flush();
            stream.Flush(flushToDisk: true);
            return header;
        }
    }

    private static void WritePadded(FileStream stream, long targetOffset)
    {
        long gap = targetOffset - stream.Position;
        if (gap < 0)
        {
            throw new InvalidOperationException("Segment sections were written out of order.");
        }

        if (gap == 0)
        {
            return;
        }

        Span<byte> padding = stackalloc byte[(int)gap];
        padding.Clear();
        stream.Write(padding);
    }
}
