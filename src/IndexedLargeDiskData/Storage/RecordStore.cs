using IndexedLargeDiskData.Caching;

namespace IndexedLargeDiskData.Storage;

/// <summary>
/// An append-only store of fixed-width records addressed by ordinal.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <remarks>
/// Because every record is the same width there is no offset table and no per-record header: record
/// <c>n</c> lives at byte <c>n * T.Size</c>. That is what lets an index entry be a bare 8-byte
/// ordinal, and what makes a full scan a pure sequential read.
/// </remarks>
public sealed class RecordStore<T> : IDisposable
    where T : IFixedRecord<T>
{
    private const int MaxRecordSize = 512;

    private readonly SegmentedFileSet _files;
    private readonly Lock _appendLock = new();
    private bool _disposed;

    internal RecordStore(string directory, StoreOptions options, BlockCache cache)
    {
        if (T.Size is <= 0 or > MaxRecordSize)
        {
            throw new NotSupportedException($"Record size must be between 1 and {MaxRecordSize} bytes.");
        }

        // Aligning the segment cap to a whole number of records keeps any single record inside one
        // file, so a read never has to stitch bytes across two handles.
        long segmentSize = options.SegmentSize / T.Size * T.Size;
        if (segmentSize < T.Size)
        {
            throw new ArgumentException("SegmentSize is smaller than a single record.", nameof(options));
        }

        int writeBuffer = Math.Max(options.WriteBufferBytes / T.Size * T.Size, T.Size);

        _files = new SegmentedFileSet(directory, "data", ".dat", segmentSize, writeBuffer, cache);

        if (_files.Length % T.Size != 0)
        {
            // A torn append at the tail; drop the partial record.
            _files.TruncateTo(_files.Length / T.Size * T.Size);
        }
    }

    /// <summary>Gets the number of records in the store, including any not yet flushed.</summary>
    public long Count => _files.Length / T.Size;

    /// <summary>Appends one record and returns its ordinal.</summary>
    public long Append(in T record)
    {
        Span<byte> scratch = stackalloc byte[T.Size];
        T.Write(scratch, record);

        lock (_appendLock)
        {
            long ordinal = _files.Length / T.Size;
            _files.Append(scratch);
            return ordinal;
        }
    }

    /// <summary>Appends a batch of records and returns the ordinal of the first one.</summary>
    public long AppendRange(ReadOnlySpan<T> records)
    {
        if (records.IsEmpty)
        {
            return Count;
        }

        byte[] scratch = new byte[records.Length * T.Size];
        for (int i = 0; i < records.Length; i++)
        {
            T.Write(scratch.AsSpan(i * T.Size), records[i]);
        }

        lock (_appendLock)
        {
            long first = _files.Length / T.Size;
            _files.Append(scratch);
            return first;
        }
    }

    /// <summary>Reads the record at <paramref name="ordinal"/>.</summary>
    public T Read(long ordinal)
    {
        if ((ulong)ordinal >= (ulong)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        Span<byte> scratch = stackalloc byte[T.Size];
        _files.Read(ordinal * T.Size, scratch);
        return T.Read(scratch);
    }

    /// <summary>Reads a contiguous run of records starting at <paramref name="startOrdinal"/>.</summary>
    public void ReadRange(long startOrdinal, Span<T> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        if (startOrdinal < 0 || startOrdinal + destination.Length > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startOrdinal));
        }

        byte[] scratch = new byte[destination.Length * T.Size];
        _files.Read(startOrdinal * T.Size, scratch);

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = T.Read(scratch.AsSpan(i * T.Size));
        }
    }

    /// <summary>Pushes buffered records to the operating system.</summary>
    /// <param name="fsync">When true, also forces them onto the storage device.</param>
    public void Flush(bool fsync = true) => _files.Flush(fsync);

    /// <summary>Drops every record at or after <paramref name="count"/>.</summary>
    internal void TruncateTo(long count) => _files.TruncateTo(count * T.Size);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _files.Dispose();
    }
}
