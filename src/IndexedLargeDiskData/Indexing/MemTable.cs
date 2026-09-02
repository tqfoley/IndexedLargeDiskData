namespace IndexedLargeDiskData.Indexing;

/// <summary>
/// The in-memory buffer of index entries that have not been written to a segment yet.
/// </summary>
/// <remarks>
/// Kept as a large sorted region plus a short unsorted tail. Appends go to the tail at O(1); when the
/// tail fills it is sorted and merged into the sorted region in one linear pass. A lookup binary
/// searches the sorted region and scans the tail, so recent appends are visible immediately without
/// paying to re-sort the whole buffer on every write.
/// </remarks>
internal sealed class MemTable
{
    private readonly int _tailCapacity;
    private readonly IndexEntry[] _tail;

    private IndexEntry[] _sorted;
    private IndexEntry[] _spare;
    private int _sortedCount;
    private int _tailCount;

    internal MemTable(int tailCapacity = 8192)
    {
        _tailCapacity = tailCapacity;
        _tail = new IndexEntry[tailCapacity];
        _sorted = [];
        _spare = [];
    }

    /// <summary>Gets the number of buffered entries.</summary>
    internal int Count => _sortedCount + _tailCount;

    /// <summary>Buffers one entry.</summary>
    internal void Add(ulong key, ulong ordinal)
    {
        _tail[_tailCount++] = new IndexEntry(key, ordinal);
        if (_tailCount == _tailCapacity)
        {
            Compact();
        }
    }

    /// <summary>Appends every buffered ordinal stored under <paramref name="key"/>.</summary>
    internal int Lookup(ulong key, List<ulong> results)
    {
        int found = 0;

        int lo = 0;
        int hi = _sortedCount;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_sorted[mid].Key < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int i = lo; i < _sortedCount && _sorted[i].Key == key; i++)
        {
            results.Add(_sorted[i].Ordinal);
            found++;
        }

        for (int i = 0; i < _tailCount; i++)
        {
            if (_tail[i].Key == key)
            {
                results.Add(_tail[i].Ordinal);
                found++;
            }
        }

        return found;
    }

    /// <summary>Sorts everything and returns the backing array with its live length.</summary>
    /// <remarks>The array stays owned by the memtable and is only valid until the next mutation.</remarks>
    internal (IndexEntry[] Entries, int Count) Drain()
    {
        Compact();
        return (_sorted, _sortedCount);
    }

    /// <summary>Discards every buffered entry.</summary>
    internal void Clear()
    {
        _sortedCount = 0;
        _tailCount = 0;
    }

    /// <summary>Streams the sorted entries, for handing to <see cref="IndexSegmentWriter"/>.</summary>
    internal static IEnumerable<IndexEntry> Enumerate(IndexEntry[] entries, int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return entries[i];
        }
    }

    private void Compact()
    {
        if (_tailCount == 0)
        {
            return;
        }

        Array.Sort(_tail, 0, _tailCount);

        int total = _sortedCount + _tailCount;
        if (_spare.Length < total)
        {
            _spare = new IndexEntry[Math.Max(total, Math.Max(_tailCapacity * 2, _spare.Length * 2))];
        }

        int a = 0;
        int b = 0;
        int o = 0;
        while (a < _sortedCount && b < _tailCount)
        {
            if (_sorted[a].CompareTo(_tail[b]) <= 0)
            {
                _spare[o++] = _sorted[a++];
            }
            else
            {
                _spare[o++] = _tail[b++];
            }
        }

        while (a < _sortedCount)
        {
            _spare[o++] = _sorted[a++];
        }

        while (b < _tailCount)
        {
            _spare[o++] = _tail[b++];
        }

        (_sorted, _spare) = (_spare, _sorted);
        _sortedCount = total;
        _tailCount = 0;
    }
}
