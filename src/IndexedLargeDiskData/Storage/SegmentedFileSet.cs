using IndexedLargeDiskData.Caching;
using Microsoft.Win32.SafeHandles;

namespace IndexedLargeDiskData.Storage;

/// <summary>
/// One logical append-only byte stream spread across a directory of capped segment files.
/// </summary>
/// <remarks>
/// <para>
/// Callers see a single contiguous address space; the set maps an offset onto
/// <c>{prefix}{index:D6}{extension}</c> and back. Segments are capped so that a terabyte-scale store
/// stays as a few thousand ordinary files that can be archived, checksummed or moved individually.
/// </para>
/// <para>
/// Appends land in a write buffer and are pushed to the operating system a block at a time. Reads
/// below the durable length go through the shared <see cref="BlockCache"/>; reads above it are served
/// from the buffer, so a record is visible to readers the moment it is appended.
/// </para>
/// </remarks>
internal sealed class SegmentedFileSet : IDisposable
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly string _extension;
    private readonly BlockCache _cache;
    private readonly List<CachedFile> _segments = [];
    private readonly byte[] _buffer;
    private readonly Lock _sync = new();

    private int _bufferLength;
    private ulong _durableLength;
    private bool _disposed;

    internal SegmentedFileSet(
        string directory,
        string prefix,
        string extension,
        ulong segmentSize,
        int writeBufferBytes,
        BlockCache cache)
    {
        _directory = directory;
        _prefix = prefix;
        _extension = extension;
        _cache = cache;
        SegmentSize = segmentSize;
        _buffer = new byte[writeBufferBytes];

        Directory.CreateDirectory(directory);
        OpenExistingSegments();
    }

    /// <summary>Gets the maximum size of a single segment file in bytes.</summary>
    internal ulong SegmentSize { get; }

    /// <summary>Gets the number of segment files currently open.</summary>
    internal int SegmentCount => _segments.Count;

    /// <summary>Gets the logical length in bytes, including data still sitting in the write buffer.</summary>
    internal ulong Length => Interlocked.Read(ref _durableLength) + (ulong)Volatile.Read(ref _bufferLength);

    /// <summary>Appends bytes at the end of the stream.</summary>
    internal void Append(ReadOnlySpan<byte> source)
    {
        lock (_sync)
        {
            while (!source.IsEmpty)
            {
                if (_bufferLength == _buffer.Length)
                {
                    FlushBufferLocked(fsync: false);
                }

                int take = Math.Min(source.Length, _buffer.Length - _bufferLength);
                source[..take].CopyTo(_buffer.AsSpan(_bufferLength));
                _bufferLength += take;
                source = source[take..];
            }
        }
    }

    /// <summary>Reads <paramref name="destination"/>.Length bytes starting at <paramref name="offset"/>.</summary>
    internal void Read(ulong offset, Span<byte> destination)
    {
        if (offset + (ulong)destination.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Read extends past the end of the data.");
        }

        ulong durable = Interlocked.Read(ref _durableLength);

        while (!destination.IsEmpty)
        {
            if (offset >= durable)
            {
                ReadFromBuffer(offset, destination);
                return;
            }

            int copied = ReadFromDisk(offset, destination, durable);
            offset += (ulong)copied;
            destination = destination[copied..];
        }
    }

    /// <summary>Pushes buffered bytes to the operating system, optionally forcing them to the device.</summary>
    internal void Flush(bool fsync)
    {
        lock (_sync)
        {
            FlushBufferLocked(fsync);
        }
    }

    /// <summary>
    /// Discards everything past <paramref name="length"/>, used to drop a torn tail during recovery.
    /// </summary>
    internal void TruncateTo(ulong length)
    {
        lock (_sync)
        {
            if (length > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Cannot truncate to a longer length.");
            }

            _bufferLength = 0;

            int keptSegments = (int)(length / SegmentSize);
            ulong tail = length % SegmentSize;
            if (tail > 0)
            {
                keptSegments++;
            }

            for (int i = _segments.Count - 1; i >= keptSegments; i--)
            {
                string path = _segments[i].Path;
                _segments[i].Dispose();
                _segments.RemoveAt(i);
                File.Delete(path);
            }

            if (tail > 0 && _segments.Count == keptSegments)
            {
                CachedFile last = _segments[^1];
                RandomAccess.SetLength(last.Handle, (long)tail);
                last.SetLength(tail);
                last.InvalidateAll();
            }

            Interlocked.Exchange(ref _durableLength, length);
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

        lock (_sync)
        {
            FlushBufferLocked(fsync: true);
            foreach (CachedFile segment in _segments)
            {
                segment.Dispose();
            }

            _segments.Clear();
        }
    }

    private void OpenExistingSegments()
    {
        ulong total = 0;
        for (int index = 0; ; index++)
        {
            string path = SegmentPath(index);
            if (!File.Exists(path))
            {
                break;
            }

            CachedFile segment = OpenSegment(path);
            _segments.Add(segment);

            if (segment.Length > SegmentSize)
            {
                throw new InvalidDataException($"Segment '{path}' is larger than the configured segment size.");
            }

            if (segment.Length < SegmentSize && File.Exists(SegmentPath(index + 1)))
            {
                throw new InvalidDataException($"Segment '{path}' is short but is not the last segment.");
            }

            total += segment.Length;
        }

        _durableLength = total;
    }

    private CachedFile OpenSegment(string path)
    {
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.None);

        return _cache.Register(handle, path);
    }

    private string SegmentPath(int index) =>
        Path.Combine(_directory, $"{_prefix}{index:D6}{_extension}");

    private void ReadFromBuffer(ulong offset, Span<byte> destination)
    {
        lock (_sync)
        {
            ulong durable = _durableLength;
            if (offset < durable)
            {
                // A flush raced us; retry through the normal path now that the bytes are on disk.
                Read(offset, destination);
                return;
            }

            int start = (int)(offset - durable);
            if (start + destination.Length > _bufferLength)
            {
                throw new InvalidOperationException("Read extends past the end of the write buffer.");
            }

            _buffer.AsSpan(start, destination.Length).CopyTo(destination);
        }
    }

    private int ReadFromDisk(ulong offset, Span<byte> destination, ulong durable)
    {
        int segmentIndex = (int)(offset / SegmentSize);
        ulong inSegment = offset % SegmentSize;

        CachedFile segment;
        lock (_sync)
        {
            segment = _segments[segmentIndex];
        }

        ulong blockIndex = inSegment / (ulong)_cache.BlockSize;
        int inBlock = (int)(inSegment % (ulong)_cache.BlockSize);

        using BlockLease lease = segment.Acquire(blockIndex);
        int available = lease.Length - inBlock;
        if (available <= 0)
        {
            throw new InvalidDataException($"Segment '{segment.Path}' is shorter than expected.");
        }

        ulong room = Math.Min((ulong)available, durable - offset);
        int take = (int)Math.Min((ulong)destination.Length, room);
        lease.Span.Slice(inBlock, take).CopyTo(destination);
        return take;
    }

    private void FlushBufferLocked(bool fsync)
    {
        if (_bufferLength > 0)
        {
            ReadOnlySpan<byte> pending = _buffer.AsSpan(0, _bufferLength);
            ulong offset = _durableLength;

            while (!pending.IsEmpty)
            {
                int segmentIndex = (int)(offset / SegmentSize);
                ulong inSegment = offset % SegmentSize;
                CachedFile segment = EnsureSegment(segmentIndex);

                int take = (int)Math.Min((ulong)pending.Length, SegmentSize - inSegment);
                RandomAccess.Write(segment.Handle, pending[..take], (long)inSegment);

                ulong newLength = inSegment + (ulong)take;
                segment.SetLength(newLength);
                InvalidateRange(segment, inSegment, take);

                offset += (ulong)take;
                pending = pending[take..];
            }

            _bufferLength = 0;
            Interlocked.Exchange(ref _durableLength, offset);
        }

        if (fsync)
        {
            foreach (CachedFile segment in _segments)
            {
                RandomAccess.FlushToDisk(segment.Handle);
            }
        }
    }

    private void InvalidateRange(CachedFile segment, ulong offset, int count)
    {
        ulong first = offset / (ulong)_cache.BlockSize;
        ulong last = (offset + (ulong)count - 1) / (ulong)_cache.BlockSize;
        for (ulong block = first; block <= last; block++)
        {
            segment.Invalidate(block);
        }
    }

    private CachedFile EnsureSegment(int index)
    {
        while (_segments.Count <= index)
        {
            _segments.Add(OpenSegment(SegmentPath(_segments.Count)));
        }

        return _segments[index];
    }
}
