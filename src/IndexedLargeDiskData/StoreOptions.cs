namespace IndexedLargeDiskData;

/// <summary>
/// Tuning knobs shared by every store in a <see cref="DataRoot"/>.
/// </summary>
/// <remarks>
/// The defaults target the design point this library was written for: terabytes of append-only
/// fixed-length records on NVMe, served through a ~20 GiB process-local cache.
/// </remarks>
public sealed class StoreOptions
{
    /// <summary>Size in bytes of a cache block, and therefore of every read issued to the OS.</summary>
    /// <remarks>
    /// Must be a power of two and at least 512. Smaller blocks cut read amplification for point
    /// lookups but raise per-block cache bookkeeping: a 20 GiB budget is 5.2M blocks at 4 KiB.
    /// </remarks>
    public int BlockSize { get; init; } = 4096;

    /// <summary>Total bytes of native memory the shared block cache may hold. Committed up front.</summary>
    public ulong CacheBudgetBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    /// <summary>Maximum bytes in a single data segment file before a new one is started.</summary>
    /// <remarks>Rounded down to a whole number of records so no record ever straddles two files.</remarks>
    public ulong SegmentSize { get; init; } = 1L << 30;

    /// <summary>Number of index entries buffered in memory before they are flushed as a sorted segment.</summary>
    /// <remarks>
    /// Also bounds crash recovery work: after a restart the index replays at most this many records.
    /// Raise it substantially (tens of millions) during a bulk load.
    /// </remarks>
    public int MemTableEntries { get; init; } = 1 << 20;

    /// <summary>Number of segments that accumulate at one level before they are merged into the next.</summary>
    public int MergeFanout { get; init; } = 4;

    /// <summary>Hard cap on the entries in one index segment, which caps peak memory while writing it.</summary>
    /// <remarks>
    /// A segment's Bloom filter has to be fully resident while the segment is written, since its bits
    /// are scattered and cannot be streamed. At the default of 128M entries and 10 bits per key that
    /// is about 160 MiB. A merge that would exceed the cap spills into several output segments.
    /// </remarks>
    public ulong MaxSegmentEntries { get; init; } = 1L << 27;

    /// <summary>One in-memory fence key is kept per this many index entries.</summary>
    /// <remarks>4096 costs 8 bytes of RAM per 64 KiB of index and narrows a lookup to one window.</remarks>
    public int FenceStride { get; init; } = 4096;

    /// <summary>Bloom filter bits per key, or zero to write no filter.</summary>
    /// <remarks>10 bits gives roughly a 1% false positive rate, which is what keeps a point lookup
    /// from having to binary-search every segment in the level tree.</remarks>
    public int BloomBitsPerKey { get; init; } = 10;

    /// <summary>Bytes buffered in memory before an append is pushed to the operating system.</summary>
    public int WriteBufferBytes { get; init; } = 1 << 20;

    /// <summary>
    /// Whether the parameterless <c>Maintain()</c> writes a progress meter to the console.
    /// </summary>
    /// <remarks>
    /// On by default because a merge can run for minutes with nothing else to show for it. Set false
    /// for a library or service host that owns its own output; passing an explicit progress callback
    /// to <c>Maintain</c> bypasses the console regardless of this setting.
    /// </remarks>
    public bool ReportMaintenanceProgress { get; init; } = true;

    public void ensureProductionValues()
    {
        StoreOptions options = new StoreOptions()
        {
            BlockSize = 4096,
            CacheBudgetBytes = 6L * 1024 * 1024, 
            SegmentSize = 5L * 1024 * 1024,
            MemTableEntries = 1 << 16,
            MaxSegmentEntries = 1 << 20,
            MergeFanout = 4,
            FenceStride = 4096,
            BloomBitsPerKey = 10,
            WriteBufferBytes = 1 << 20,
        };

        if(this.BlockSize != options.BlockSize)
        {
            throw new Exception("bad");
        }
        if (this.CacheBudgetBytes != options.CacheBudgetBytes)
        {
            throw new Exception("bad");
        }
        if (this.SegmentSize != options.SegmentSize)
        {
            throw new Exception("bad");
        }
        if (this.BlockSize != options.BlockSize)
        {
            throw new Exception("bad");
        }
        if (this.MemTableEntries != options.MemTableEntries)
        {
            throw new Exception("bad");
        }
        if (this.MaxSegmentEntries != options.MaxSegmentEntries)
        {
            throw new Exception("bad");
        }
        if (this.FenceStride != options.FenceStride)
        {
            throw new Exception("bad");
        }
        if (this.WriteBufferBytes != options.WriteBufferBytes)
        {
            throw new Exception("bad");
        }
    }
    
    internal void Validate()
    {
        //ensureProductionValues();

        if (BlockSize < 512 || (BlockSize & (BlockSize - 1)) != 0)
            throw new ArgumentException("BlockSize must be a power of two >= 512.", nameof(BlockSize));
        if (CacheBudgetBytes < (ulong)BlockSize * 16UL)
            throw new ArgumentException("CacheBudgetBytes must hold at least 16 blocks.", nameof(CacheBudgetBytes));
        if (SegmentSize < (ulong)BlockSize)
            throw new ArgumentException("SegmentSize must be at least one block.", nameof(SegmentSize));
        if (MemTableEntries < 1024)
            throw new ArgumentException("MemTableEntries must be at least 1024.", nameof(MemTableEntries));
        if (MergeFanout < 2)
            throw new ArgumentException("MergeFanout must be at least 2.", nameof(MergeFanout));
        if (MaxSegmentEntries < (ulong)MemTableEntries)
            throw new ArgumentException("MaxSegmentEntries must be at least MemTableEntries.", nameof(MaxSegmentEntries));
        if (FenceStride < 16)
            throw new ArgumentException("FenceStride must be at least 16.", nameof(FenceStride));
        if (BloomBitsPerKey < 0 || BloomBitsPerKey > 32)
            throw new ArgumentException("BloomBitsPerKey must be between 0 and 32.", nameof(BloomBitsPerKey));
        if (WriteBufferBytes < BlockSize)
            throw new ArgumentException("WriteBufferBytes must be at least one block.", nameof(WriteBufferBytes));
    }
}
