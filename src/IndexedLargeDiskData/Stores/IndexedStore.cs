using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Indexing;
using IndexedLargeDiskData.Storage;

namespace IndexedLargeDiskData.Stores;

/// <summary>
/// An append-only record store with one or more secondary indexes over its records.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <remarks>
/// <para>
/// Records are the source of truth. Indexes are derived, so the durability rule is simply that an
/// index may never claim to cover records that are not committed yet: before any index is flushed,
/// the records it covers are forced to disk and the committed count is written. A crash therefore
/// costs at most a memtable's worth of replay, never a corrupt index.
/// </para>
/// <para>
/// Instances are safe for one writer and many concurrent readers.
/// </para>
/// </remarks>
public abstract class IndexedStore<T> : IDisposable
    where T : IFixedRecord<T>
{
    private const string ManifestFile = "manifest";

    private readonly string _directory;
    private readonly SortedIndex[] _indexes;
    private readonly Lock _appendLock = new();
    private ulong _committedCount;
    private bool _disposed;

    /// <summary>Opens or creates a store under <paramref name="directory"/>.</summary>
    /// <param name="directory">The store directory. Created if missing.</param>
    /// <param name="options">Tuning shared with the rest of the data root.</param>
    /// <param name="cache">The shared block cache.</param>
    /// <param name="indexNames">One name per secondary index, in the order <see cref="GetKey"/> expects.</param>
    protected IndexedStore(string directory, StoreOptions options, BlockCache cache, params string[] indexNames)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(indexNames);

        _directory = directory;
        Options = options;
        Directory.CreateDirectory(directory);

        Records = new RecordStore<T>(Path.Combine(directory, "data"), options, cache);
        _indexes = [.. indexNames.Select(name => new SortedIndex(Path.Combine(directory, "index-" + name), options, cache))];

        Recover();
    }

    /// <summary>Gets the number of records in the store.</summary>
    public ulong Count => Records.Count;

    /// <summary>Gets the number of records that have been forced to disk and committed.</summary>
    public ulong CommittedCount => Interlocked.Read(ref _committedCount);

    /// <summary>Gets the options this store was opened with.</summary>
    protected StoreOptions Options { get; }

    /// <summary>Gets the underlying record store.</summary>
    protected RecordStore<T> Records { get; }

    /// <summary>Extracts the key that index <paramref name="index"/> stores for <paramref name="record"/>.</summary>
    /// <param name="index">The zero-based index number, matching the constructor's name order.</param>
    /// <param name="record">The record being indexed.</param>
    protected abstract ulong GetKey(int index, in T record);

    /// <summary>Appends a record and returns its ordinal.</summary>
    public ulong Append(in T record)
    {
        ulong ordinal;
        lock (_appendLock)
        {
            ordinal = Records.Append(record);
            for (int i = 0; i < _indexes.Length; i++)
            {
                _indexes[i].Add(GetKey(i, record), ordinal);
            }
        }

        FlushFullIndexes();
        return ordinal;
    }

    /// <summary>Appends a batch of records and returns the ordinal of the first.</summary>
    public ulong AppendRange(ReadOnlySpan<T> records)
    {
        if (records.IsEmpty)
        {
            return Count;
        }

        ulong first;
        lock (_appendLock)
        {
            first = Records.AppendRange(records);
            for (int r = 0; r < records.Length; r++)
            {
                for (int i = 0; i < _indexes.Length; i++)
                {
                    _indexes[i].Add(GetKey(i, records[r]), first + (ulong)r);
                }
            }
        }

        FlushFullIndexes();
        return first;
    }

    /// <summary>Reads the record at <paramref name="ordinal"/>.</summary>
    public T Read(ulong ordinal) => Records.Read(ordinal);

    /// <summary>Forces records to disk, commits the count, then flushes every index.</summary>
    public void Flush()
    {
        ulong committed = CommitRecords();
        foreach (SortedIndex index in _indexes)
        {
            index.Flush(committed);
        }
    }

    /// <summary>Gets the store's directory name, used to label progress reports.</summary>
    public string Name => Path.GetFileName(_directory);

    /// <summary>Estimates the index entries a <see cref="Maintain()"/> pass would write.</summary>
    /// <remarks>Zero means every level is already under the fanout and a pass would do nothing.</remarks>
    public ulong PlannedMaintenanceEntries =>
        _indexes.Aggregate(0UL, (total, i) => total + i.PlannedMergeEntries());

    /// <summary>Runs index tier merges until every level is back under the fanout.</summary>
    /// <remarks>
    /// Reports progress to the console unless <see cref="StoreOptions.ReportMaintenanceProgress"/>
    /// is false. Use the <see cref="Maintain(Action{MaintenanceProgress})"/> overload to route it
    /// somewhere else.
    /// </remarks>
    public void Maintain()
    {
        if (!Options.ReportMaintenanceProgress)
        {
            MaintainCore(null);
            return;
        }

        ulong total = PlannedMaintenanceEntries;
        if (total == 0)
        {
            ConsoleMaintenanceReporter.ReportNothingToDo();
            return;
        }

        ConsoleMaintenanceReporter reporter = new(total);
        MaintainCore(new MergeProgressTracker(total, reporter.Report));
        reporter.Finish(total);
    }

    /// <summary>Runs index tier merges, reporting progress as it goes.</summary>
    /// <param name="progress">
    /// Called on the calling thread as merges advance, roughly every 65,536 entries written. A plain
    /// delegate rather than <see cref="IProgress{T}"/> so reports stay synchronous and ordered: a
    /// console meter wants them in sequence, not marshalled onto another context.
    /// </param>
    public void Maintain(Action<MaintenanceProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        MaintainCore(new MergeProgressTracker(PlannedMaintenanceEntries, progress));
    }

    internal void MaintainCore(MergeProgressTracker? tracker)
    {
        foreach (SortedIndex index in _indexes)
        {
            if (tracker is null)
            {
                index.Maintain();
                continue;
            }

            string stage = $"{Name}/{index.Name}";
            index.Maintain(entries => tracker.Advance(stage, entries));
        }
    }

    /// <summary>Gets a snapshot of per-index statistics, for diagnostics.</summary>
    public IReadOnlyList<IndexStatistics> GetIndexStatistics() =>
        [.. _indexes.Select(i => new IndexStatistics(i.Name, i.SegmentCount, i.PendingCount, i.CoveredUpTo))];

    /// <summary>Collects the ordinals recorded under <paramref name="key"/> in index <paramref name="index"/>.</summary>
    /// <returns>Ordinals in ascending order, which is also the cheapest order to read them back in.</returns>
    protected List<ulong> FindOrdinals(int index, ulong key)
    {
        List<ulong> results = [];
        _indexes[index].Lookup(key, results);
        results.Sort();
        return results;
    }

    /// <summary>Counts the records whose index <paramref name="index"/> key equals <paramref name="key"/>.</summary>
    /// <remarks>
    /// Answered from the index alone. <see cref="FindRecords"/> has to read every matching record out
    /// of the data files, which at a thousand matches is a thousand random reads; a count only needs
    /// the ordinals, so it never touches the records at all.
    /// </remarks>
    protected ulong CountMatching(int index, ulong key)
    {
        List<ulong> ordinals = [];
        return (ulong)_indexes[index].Lookup(key, ordinals);
    }

    /// <summary>Reads every record whose index <paramref name="index"/> key equals <paramref name="key"/>.</summary>
    protected List<T> FindRecords(int index, ulong key)
    {
        List<ulong> ordinals = FindOrdinals(index, key);
        List<T> records = new(ordinals.Count);
        foreach (ulong ordinal in ordinals)
        {
            records.Add(Records.Read(ordinal));
        }

        return records;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the store's files and indexes.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;

        try
        {
            Flush();
        }
        finally
        {
            foreach (SortedIndex index in _indexes)
            {
                index.Dispose();
            }

            Records.Dispose();
        }
    }

    /// <summary>Forces buffered records to disk and records the new committed count.</summary>
    private ulong CommitRecords()
    {
        lock (_appendLock)
        {
            Records.Flush(fsync: true);
            ulong count = Records.Count;
            if (count != Interlocked.Read(ref _committedCount))
            {
                WriteManifest(count);
                Interlocked.Exchange(ref _committedCount, count);
            }

            return count;
        }
    }

    /// <summary>
    /// Flushes any index whose memtable has filled, committing the records it covers first.
    /// </summary>
    private void FlushFullIndexes()
    {
        if (!_indexes.Any(i => i.NeedsFlush))
        {
            return;
        }

        ulong committed = CommitRecords();
        foreach (SortedIndex index in _indexes)
        {
            if (index.NeedsFlush)
            {
                index.Flush(committed);
            }
        }
    }

    private void Recover()
    {
        ulong committed = ReadManifest();

        // Records past the committed count reached the disk but were never acknowledged; an index
        // may not reference them, so drop them rather than leave dangling ordinals.
        if (Records.Count > committed)
        {
            Records.TruncateTo(committed);
        }
        else if (Records.Count < committed)
        {
            throw new InvalidDataException(
                $"Store '{_directory}' claims {committed} records but only {Records.Count} are present.");
        }

        Interlocked.Exchange(ref _committedCount, committed);

        // Rebuild the tail of each index that never made it into a segment.
        for (int i = 0; i < _indexes.Length; i++)
        {
            SortedIndex index = _indexes[i];
            for (ulong ordinal = index.CoveredUpTo; ordinal < committed; ordinal++)
            {
                T record = Records.Read(ordinal);
                index.Add(GetKey(i, record), ordinal);
            }
        }
    }

    private string ManifestPath => Path.Combine(_directory, ManifestFile);

    private ulong ReadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return 0;
        }

        foreach (string line in File.ReadAllLines(ManifestPath))
        {
            if (line.StartsWith("records=", StringComparison.Ordinal) &&
                ulong.TryParse(line["records=".Length..], out ulong value))
            {
                return value;
            }
        }

        throw new InvalidDataException($"Manifest '{ManifestPath}' is unreadable.");
    }

    private void WriteManifest(ulong count)
    {
        string temp = ManifestPath + ".tmp";
        File.WriteAllText(temp, $"records={count}\n");
        File.Move(temp, ManifestPath, overwrite: true);
    }
}

/// <summary>A point-in-time view of one index, for diagnostics and tests.</summary>
/// <param name="Name">The index name.</param>
/// <param name="SegmentCount">How many immutable segments are live.</param>
/// <param name="PendingCount">How many entries are buffered in memory.</param>
/// <param name="CoveredUpTo">The exclusive record ordinal the on-disk segments cover.</param>
public readonly record struct IndexStatistics(string Name, int SegmentCount, int PendingCount, ulong CoveredUpTo);
