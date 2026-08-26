using IndexedLargeDiskData.Caching;

namespace IndexedLargeDiskData.Indexing;

/// <summary>
/// A duplicate-tolerant index from a 64-bit key to record ordinals, built from immutable sorted runs.
/// </summary>
/// <remarks>
/// <para>
/// Entries buffer in a <see cref="MemTable"/>, flush to a level-0 segment when it fills, and merge
/// upward in tiers of <see cref="StoreOptions.MergeFanout"/>. A lookup probes every live segment plus
/// the memtable; per-segment key ranges and Bloom filters keep all but a couple of those probes to a
/// single block read.
/// </para>
/// <para>
/// Nothing here needs to be crash safe. The records are the source of truth and are append-only, so a
/// missing tail of index entries is rebuilt on open by replaying records from
/// <see cref="CoveredUpTo"/>. That is why a flush can be a plain write with no journal in front of it.
/// </para>
/// </remarks>
internal sealed class SortedIndex : IDisposable
{
    private const string PendingCommitFile = "pending.commit";
    private const string SegmentExtension = ".idx";
    private const string TempExtension = ".idx.tmp";

    private readonly string _directory;
    private readonly StoreOptions _options;
    private readonly BlockCache _cache;
    private readonly ReaderWriterLockSlim _segmentLock = new(LockRecursionPolicy.NoRecursion);
    private readonly Lock _memLock = new();
    private readonly Lock _writeLock = new();

    private IndexSegment[] _segments = [];
    private MemTable _memTable = new();
    private MemTable? _frozen;
    private int _nextSegmentId;
    private long _coveredUpTo;
    private bool _disposed;

    internal SortedIndex(string directory, StoreOptions options, BlockCache cache)
    {
        _directory = directory;
        _options = options;
        _cache = cache;

        Directory.CreateDirectory(directory);
        CompletePendingCommit();
        DiscardTemporaries();
        LoadSegments();
    }

    /// <summary>Gets the name of the index, taken from its directory.</summary>
    internal string Name => Path.GetFileName(_directory);

    /// <summary>Gets the exclusive record ordinal that this index is complete up to on disk.</summary>
    /// <remarks>Records at or after this point must be replayed into the memtable after a restart.</remarks>
    internal long CoveredUpTo => Interlocked.Read(ref _coveredUpTo);

    /// <summary>Gets the number of live segments.</summary>
    internal int SegmentCount => Volatile.Read(ref _segments).Length;

    /// <summary>Gets the number of entries buffered in memory.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_memLock)
            {
                return _memTable.Count;
            }
        }
    }

    /// <summary>Gets a value indicating whether the memtable has reached its flush threshold.</summary>
    internal bool NeedsFlush => PendingCount >= _options.MemTableEntries;

    /// <summary>Buffers an entry mapping <paramref name="key"/> to <paramref name="ordinal"/>.</summary>
    internal void Add(long key, long ordinal)
    {
        lock (_memLock)
        {
            _memTable.Add(key, ordinal);
        }
    }

    /// <summary>Appends every ordinal recorded under <paramref name="key"/> to <paramref name="results"/>.</summary>
    /// <returns>The number of ordinals appended.</returns>
    internal int Lookup(long key, List<long> results)
    {
        int found = 0;

        _segmentLock.EnterReadLock();
        try
        {
            foreach (IndexSegment segment in _segments)
            {
                found += segment.Lookup(key, results);
            }

            lock (_memLock)
            {
                if (_frozen is not null)
                {
                    found += _frozen.Lookup(key, results);
                }

                found += _memTable.Lookup(key, results);
            }
        }
        finally
        {
            _segmentLock.ExitReadLock();
        }

        return found;
    }

    /// <summary>Writes the memtable out as a new level-0 segment.</summary>
    /// <param name="coveredUpTo">The exclusive record ordinal the index is complete up to after this flush.</param>
    internal void Flush(long coveredUpTo)
    {
        lock (_writeLock)
        {
            MemTable frozen;
            lock (_memLock)
            {
                if (_memTable.Count == 0)
                {
                    Interlocked.Exchange(ref _coveredUpTo, Math.Max(_coveredUpTo, coveredUpTo));
                    return;
                }

                frozen = _memTable;
                _frozen = frozen;
                _memTable = new MemTable();
            }

            (IndexEntry[] entries, int count) = frozen.Drain();

            int id = _nextSegmentId++;
            IndexSegmentWriter.Write(
                TempPath(id),
                MemTable.Enumerate(entries, count),
                count,
                level: 0,
                coveredUpTo,
                _options);

            Commit([id], [], clearFrozen: true);
            Interlocked.Exchange(ref _coveredUpTo, Math.Max(_coveredUpTo, coveredUpTo));
        }
    }

    /// <summary>Merges tiers until no level holds more than <see cref="StoreOptions.MergeFanout"/> segments.</summary>
    /// <param name="onEntriesWritten">
    /// Optional sink called with the entries written since the last call, so a long pass can be
    /// reported against the estimate from <see cref="PlannedMergeEntries"/>.
    /// </param>
    internal void Maintain(Action<long>? onEntriesWritten = null)
    {
        lock (_writeLock)
        {
            while (TryMergeOnce(onEntriesWritten))
            {
            }
        }
    }

    /// <summary>
    /// Reports whether merging <paramref name="segmentCount"/> segments holding
    /// <paramref name="totalEntries"/> would actually reduce the number of segments.
    /// </summary>
    /// <remarks>
    /// A merge splits its output at <see cref="StoreOptions.MaxSegmentEntries"/>. Once segments reach
    /// that cap, merging <c>MergeFanout</c> of them emits <c>MergeFanout</c> outputs: the same number
    /// of segments, one level higher, for the cost of rewriting every entry. The cascade would then
    /// repeat forever, climbing a level per round and never terminating. Segments at the cap are
    /// simply terminal — nothing can consolidate them further — so the merge has to be declined.
    /// </remarks>
    private bool MakesProgress(long totalEntries, int segmentCount)
    {
        long outputs = Math.Max(1, (totalEntries + _options.MaxSegmentEntries - 1) / _options.MaxSegmentEntries);
        return outputs < segmentCount;
    }

    /// <summary>
    /// Estimates the index entries a full <see cref="Maintain"/> pass would write.
    /// </summary>
    /// <remarks>
    /// Replays <see cref="TryMergeOnce"/>'s selection rule over a copy of the current level shape,
    /// without touching a file. Every merge rewrites all of its inputs, so the sum of the inputs
    /// chosen across the whole cascade is exactly the work the pass will do — including the extra
    /// rounds a merge triggers by pushing segments up into an already-full level.
    /// </remarks>
    internal long PlannedMergeEntries()
    {
        Dictionary<int, List<long>> levels = [];
        foreach (IndexSegment segment in Volatile.Read(ref _segments).OrderBy(s => s.Id))
        {
            if (!levels.TryGetValue(segment.Level, out List<long>? atLevel))
            {
                levels[segment.Level] = atLevel = [];
            }

            atLevel.Add(segment.EntryCount);
        }

        long planned = 0;
        while (true)
        {
            int? target = null;
            long merged = 0;
            foreach (int level in levels.Keys.Order())
            {
                List<long> candidate = levels[level];
                if (candidate.Count < _options.MergeFanout)
                {
                    continue;
                }

                long total = 0;
                for (int i = 0; i < _options.MergeFanout; i++)
                {
                    total += candidate[i];
                }

                if (!MakesProgress(total, _options.MergeFanout))
                {
                    continue;
                }

                target = level;
                merged = total;
                break;
            }

            if (target is null)
            {
                return planned;
            }

            List<long> source = levels[target.Value];
            source.RemoveRange(0, _options.MergeFanout);
            planned += merged;

            if (!levels.TryGetValue(target.Value + 1, out List<long>? destination))
            {
                levels[target.Value + 1] = destination = [];
            }

            long remaining = merged;
            do
            {
                long take = Math.Min(remaining, _options.MaxSegmentEntries);
                destination.Add(take);
                remaining -= take;
            }
            while (remaining > 0);
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

        _segmentLock.EnterWriteLock();
        try
        {
            foreach (IndexSegment segment in _segments)
            {
                segment.Dispose();
            }

            _segments = [];
        }
        finally
        {
            _segmentLock.ExitWriteLock();
        }

        _segmentLock.Dispose();
    }

    private bool TryMergeOnce(Action<long>? onEntriesWritten)
    {
        IndexSegment[] snapshot = Volatile.Read(ref _segments);

        List<IndexSegment>? inputs = null;
        foreach (int level in snapshot.Select(s => s.Level).Distinct().Order())
        {
            List<IndexSegment> atLevel = snapshot.Where(s => s.Level == level).OrderBy(s => s.Id).ToList();
            if (atLevel.Count < _options.MergeFanout)
            {
                continue;
            }

            List<IndexSegment> candidate = atLevel.Take(_options.MergeFanout).ToList();
            if (!MakesProgress(candidate.Sum(s => s.EntryCount), candidate.Count))
            {
                continue;
            }

            inputs = candidate;
            break;
        }

        if (inputs is null)
        {
            return false;
        }

        long total = inputs.Sum(s => s.EntryCount);
        long covered = inputs.Max(s => s.CoveredUpTo);
        int outputLevel = inputs[0].Level + 1;

        List<int> outputIds = [];
        using (IEnumerator<IndexEntry> merged = Merge(inputs).GetEnumerator())
        {
            long remaining = total;
            do
            {
                long take = Math.Min(remaining, _options.MaxSegmentEntries);
                int id = _nextSegmentId++;
                outputIds.Add(id);
                IndexSegmentWriter.Write(
                    TempPath(id), TakeFrom(merged, take), take, outputLevel, covered, _options, onEntriesWritten);
                remaining -= take;
            }
            while (remaining > 0);
        }

        Commit(outputIds, inputs.Select(s => s.Id).ToList(), clearFrozen: false);
        return true;
    }

    /// <summary>
    /// Publishes new segments and retires the ones they replace, crash-safely.
    /// </summary>
    /// <remarks>
    /// The pending file makes the swap idempotent: if the process dies at any point after it is
    /// written, reopening replays the remaining renames and deletes. Without it, a crash between
    /// publishing a merge output and deleting its inputs would leave both on disk and every lookup
    /// would return each ordinal twice.
    /// </remarks>
    private void Commit(List<int> outputIds, List<int> inputIds, bool clearFrozen)
    {
        WritePendingCommit(outputIds, inputIds);

        foreach (int id in outputIds)
        {
            PromoteTemp(id);
        }

        List<IndexSegment> retired = [];
        IndexSegment[] opened = [.. outputIds.Select(id => IndexSegment.Open(SegmentPath(id), _cache))];

        _segmentLock.EnterWriteLock();
        try
        {
            HashSet<int> removing = [.. inputIds];
            List<IndexSegment> next = [];
            foreach (IndexSegment segment in _segments)
            {
                if (removing.Contains(segment.Id))
                {
                    retired.Add(segment);
                }
                else
                {
                    next.Add(segment);
                }
            }

            next.AddRange(opened);
            _segments = [.. next];

            if (clearFrozen)
            {
                lock (_memLock)
                {
                    _frozen = null;
                }
            }
        }
        finally
        {
            _segmentLock.ExitWriteLock();
        }

        foreach (IndexSegment segment in retired)
        {
            segment.Dispose();
        }

        foreach (int id in inputIds)
        {
            File.Delete(SegmentPath(id));
        }

        File.Delete(Path.Combine(_directory, PendingCommitFile));
    }

    private static IEnumerable<IndexEntry> Merge(IReadOnlyList<IndexSegment> inputs)
    {
        List<IEnumerator<IndexEntry>> cursors = [];
        try
        {
            foreach (IndexSegment segment in inputs)
            {
                IEnumerator<IndexEntry> cursor = segment.EnumerateEntries().GetEnumerator();
                if (cursor.MoveNext())
                {
                    cursors.Add(cursor);
                }
                else
                {
                    cursor.Dispose();
                }
            }

            while (cursors.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < cursors.Count; i++)
                {
                    if (cursors[i].Current.CompareTo(cursors[best].Current) < 0)
                    {
                        best = i;
                    }
                }

                yield return cursors[best].Current;

                if (!cursors[best].MoveNext())
                {
                    cursors[best].Dispose();
                    cursors.RemoveAt(best);
                }
            }
        }
        finally
        {
            foreach (IEnumerator<IndexEntry> cursor in cursors)
            {
                cursor.Dispose();
            }
        }
    }

    private static IEnumerable<IndexEntry> TakeFrom(IEnumerator<IndexEntry> source, long count)
    {
        for (long i = 0; i < count; i++)
        {
            if (!source.MoveNext())
            {
                yield break;
            }

            yield return source.Current;
        }
    }

    private string SegmentPath(int id) => Path.Combine(_directory, $"{id:D6}{SegmentExtension}");

    private string TempPath(int id) => Path.Combine(_directory, $"{id:D6}{TempExtension}");

    private void PromoteTemp(int id)
    {
        string temp = TempPath(id);
        if (File.Exists(temp))
        {
            File.Move(temp, SegmentPath(id), overwrite: true);
        }
    }

    private void WritePendingCommit(List<int> outputIds, List<int> inputIds)
    {
        string path = Path.Combine(_directory, PendingCommitFile);
        string temp = path + ".tmp";
        File.WriteAllLines(temp,
        [
            "outputs=" + string.Join(',', outputIds),
            "inputs=" + string.Join(',', inputIds),
        ]);
        File.Move(temp, path, overwrite: true);
    }

    private void CompletePendingCommit()
    {
        string path = Path.Combine(_directory, PendingCommitFile);
        if (!File.Exists(path))
        {
            return;
        }

        List<int> outputs = [];
        List<int> inputs = [];
        foreach (string line in File.ReadAllLines(path))
        {
            int split = line.IndexOf('=');
            if (split < 0)
            {
                continue;
            }

            string key = line[..split];
            List<int> target;
            if (key == "outputs")
            {
                target = outputs;
            }
            else
            {
                target = inputs;
            }
            foreach (string part in line[(split + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                target.Add(int.Parse(part));
            }
        }

        foreach (int id in outputs)
        {
            PromoteTemp(id);
        }

        foreach (int id in inputs)
        {
            File.Delete(SegmentPath(id));
        }

        File.Delete(path);
    }

    private void DiscardTemporaries()
    {
        foreach (string stray in Directory.EnumerateFiles(_directory, "*" + TempExtension))
        {
            File.Delete(stray);
        }
    }

    private void LoadSegments()
    {
        List<IndexSegment> loaded = [];
        foreach (string path in Directory.EnumerateFiles(_directory, "*" + SegmentExtension).Order())
        {
            // Windows wildcard matching is loose enough to reach the .idx.tmp files; be explicit.
            if (Path.GetExtension(path) == SegmentExtension)
            {
                loaded.Add(IndexSegment.Open(path, _cache));
            }
        }

        _segments = [.. loaded.OrderBy(s => s.Id)];
        if (_segments.Length == 0)
        {
            _nextSegmentId = 0;
            _coveredUpTo = 0;
        }
        else
        {
            _nextSegmentId = _segments.Max(s => s.Id) + 1;
            _coveredUpTo = _segments.Max(s => s.CoveredUpTo);
        }
    }
}
