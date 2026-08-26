using System.Buffers.Binary;

namespace IndexedLargeDiskData.Indexing;

/// <summary>The fixed 128-byte preamble of an index segment file.</summary>
/// <remarks>
/// Segments are self-describing on purpose: level, coverage and section offsets all live here rather
/// than in a side manifest, so the set of live segments can be recovered by listing the directory.
/// </remarks>
internal readonly struct SegmentHeader
{
    internal required long EntryCount { get; init; }

    internal required long MinKey { get; init; }

    internal required long MaxKey { get; init; }

    /// <summary>Gets the exclusive record ordinal up to which this segment's entries are complete.</summary>
    internal required long CoveredUpTo { get; init; }

    internal required int Level { get; init; }

    internal required int FenceStride { get; init; }

    internal required long FenceCount { get; init; }

    internal required int BloomBlockCount { get; init; }

    internal required long EntriesOffset { get; init; }

    internal required long FenceOffset { get; init; }

    internal required long BloomOffset { get; init; }

    /// <summary>Computes the section offsets for a segment with the given shape.</summary>
    internal static SegmentHeader Create(
        long entryCount,
        long minKey,
        long maxKey,
        long coveredUpTo,
        int level,
        int fenceStride,
        int bloomBitsPerKey)
    {
        long entriesOffset = IndexSegment.HeaderSize;
        long fenceCount;
        if (entryCount == 0)
        {
            fenceCount = 0;
        }
        else
        {
            fenceCount = (entryCount + fenceStride - 1) / fenceStride;
        }
        long fenceOffset = Align(entriesOffset + (entryCount * IndexEntry.Size));
        int bloomBlocks = BlockedBloom.BlockCount(entryCount, bloomBitsPerKey);
        long bloomOffset = Align(fenceOffset + (fenceCount * sizeof(long)));

        return new SegmentHeader
        {
            EntryCount = entryCount,
            MinKey = minKey,
            MaxKey = maxKey,
            CoveredUpTo = coveredUpTo,
            Level = level,
            FenceStride = fenceStride,
            FenceCount = fenceCount,
            BloomBlockCount = bloomBlocks,
            EntriesOffset = entriesOffset,
            FenceOffset = fenceOffset,
            BloomOffset = bloomOffset,
        };
    }

    /// <summary>Gets the total length of the segment file described by this header.</summary>
    internal long FileLength => BloomOffset + ((long)BloomBlockCount * BlockedBloom.BlockBytes);

    internal void Write(Span<byte> destination)
    {
        destination[..IndexSegment.HeaderSize].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(destination, IndexSegment.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], IndexSegment.Version);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], FenceStride);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], EntryCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], MinKey);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], MaxKey);
        BinaryPrimitives.WriteInt64LittleEndian(destination[40..], CoveredUpTo);
        BinaryPrimitives.WriteInt32LittleEndian(destination[48..], Level);
        BinaryPrimitives.WriteInt32LittleEndian(destination[52..], BloomBlockCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[56..], EntriesOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[64..], FenceOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[72..], FenceCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[80..], BloomOffset);
    }

    internal static SegmentHeader Read(ReadOnlySpan<byte> source, string path)
    {
        long magic = BinaryPrimitives.ReadInt64LittleEndian(source);
        if (magic != IndexSegment.Magic)
        {
            throw new InvalidDataException($"'{path}' is not an index segment.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
        if (version != IndexSegment.Version)
        {
            throw new InvalidDataException($"'{path}' has unsupported segment version {version}.");
        }

        return new SegmentHeader
        {
            FenceStride = BinaryPrimitives.ReadInt32LittleEndian(source[12..]),
            EntryCount = BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            MinKey = BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
            MaxKey = BinaryPrimitives.ReadInt64LittleEndian(source[32..]),
            CoveredUpTo = BinaryPrimitives.ReadInt64LittleEndian(source[40..]),
            Level = BinaryPrimitives.ReadInt32LittleEndian(source[48..]),
            BloomBlockCount = BinaryPrimitives.ReadInt32LittleEndian(source[52..]),
            EntriesOffset = BinaryPrimitives.ReadInt64LittleEndian(source[56..]),
            FenceOffset = BinaryPrimitives.ReadInt64LittleEndian(source[64..]),
            FenceCount = BinaryPrimitives.ReadInt64LittleEndian(source[72..]),
            BloomOffset = BinaryPrimitives.ReadInt64LittleEndian(source[80..]),
        };
    }

    /// <summary>Rounds an offset up so a Bloom block never straddles a cache block.</summary>
    private static long Align(long offset) =>
        (offset + BlockedBloom.BlockBytes - 1) / BlockedBloom.BlockBytes * BlockedBloom.BlockBytes;
}
