using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Indexing;

namespace IndexedLargeDiskData.Tests;

public class SortedIndexTests
{
    [Fact]
    public void Lookup_FindsEntriesStillInTheMemtable()
    {
        using Fixture fixture = new();

        fixture.Index.Add(42, 7);
        fixture.Index.Add(42, 9);
        fixture.Index.Add(43, 8);

        Assert.Equal(new ulong[] { 7, 9 }, fixture.Lookup(42));
        Assert.Equal(new ulong[] { 8 }, fixture.Lookup(43));
        Assert.Empty(fixture.Lookup(44));
    }

    [Fact]
    public void Lookup_FindsEntriesAfterAFlush()
    {
        using Fixture fixture = new();

        for (ulong ordinal = 0; ordinal < 500; ordinal++)
        {
            fixture.Index.Add(ordinal % 10, ordinal);
        }

        fixture.Index.Flush(500);

        Assert.Equal(1, fixture.Index.SegmentCount);
        Assert.Equal(0, fixture.Index.PendingCount);
        Assert.Equal(500UL, fixture.Index.CoveredUpTo);
        Assert.Equal(new ulong[] { 3, 13, 23, 33, 43 }, fixture.Lookup(3).Take(5));
        Assert.Equal(50, fixture.Lookup(3).Count);
    }

    [Fact]
    public void Lookup_SpansFlushedSegmentsAndTheLiveMemtable()
    {
        using Fixture fixture = new();

        fixture.Index.Add(5, 100);
        fixture.Index.Flush(101);
        fixture.Index.Add(5, 200);

        Assert.Equal(new ulong[] { 100, 200 }, fixture.Lookup(5));
    }

    [Fact]
    public void Flush_IsANoOpWhenNothingIsBuffered()
    {
        using Fixture fixture = new();

        fixture.Index.Flush(0);

        Assert.Equal(0, fixture.Index.SegmentCount);
    }

    [Fact]
    public void Maintain_MergesATierAndPreservesEveryEntry()
    {
        using Fixture fixture = new();
        Dictionary<ulong, List<ulong>> expected = [];

        ulong seed = 1;
        ulong ordinal = 0;
        for (int flush = 0; flush < 3; flush++)
        {
            for (int i = 0; i < 200; i++)
            {
                ulong key = (ulong)(TestData.Next(ref seed) % 50);
                fixture.Index.Add(key, ordinal);

                if (!expected.TryGetValue(key, out List<ulong>? list))
                {
                    expected[key] = list = [];
                }

                list.Add(ordinal);
                ordinal++;
            }

            fixture.Index.Flush(ordinal);
        }

        Assert.Equal(3, fixture.Index.SegmentCount);

        fixture.Index.Maintain();

        // Fanout is three, so the three level-0 segments collapse into one at level one.
        Assert.Equal(1, fixture.Index.SegmentCount);

        foreach ((ulong key, List<ulong> ordinals) in expected)
        {
            Assert.Equal(ordinals.Order(), fixture.Lookup(key));
        }
    }

    [Fact]
    public void Segments_AndTheirCoverageSurviveReopening()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        string path = Path.Combine(dir.Path, "index-test");

        using (BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes))
        using (SortedIndex index = new(path, options, cache))
        {
            for (ulong ordinal = 0; ordinal < 300; ordinal++)
            {
                index.Add(ordinal % 7, ordinal);
            }

            index.Flush(300);
        }

        using BlockCache reopenedCache = new(options.BlockSize, options.CacheBudgetBytes);
        using SortedIndex reopened = new(path, options, reopenedCache);

        Assert.Equal(1, reopened.SegmentCount);
        Assert.Equal(300UL, reopened.CoveredUpTo);

        List<ulong> results = [];
        reopened.Lookup(4, results);
        Assert.Equal(43, results.Count);
        Assert.All(results, ordinal => Assert.Equal(4UL, ordinal % 7));
    }

    [Fact]
    public void Lookup_ReturnsNothingForKeysOutsideEverySegment()
    {
        using Fixture fixture = new();

        for (ulong ordinal = 0; ordinal < 100; ordinal++)
        {
            fixture.Index.Add(ordinal, ordinal);
        }

        fixture.Index.Flush(100);

        // Keys are unsigned now, so there is no key below the range to probe; the far end of it
        // stands in for the miss the -1 used to check.
        Assert.Empty(fixture.Lookup(ulong.MaxValue));
        Assert.Empty(fixture.Lookup(1000));
    }

    [Fact]
    public void Lookup_HandlesRunsLongerThanTheFenceStride()
    {
        using Fixture fixture = new();

        // A single key repeated far past the 16-entry fence stride: the run has to be walked
        // across several fence windows.
        for (ulong ordinal = 0; ordinal < 400; ordinal++)
        {
            ulong key;
            if (ordinal < 300)
            {
                key = 1;
            }
            else
            {
                key = 2;
            }

            fixture.Index.Add(key, ordinal);
        }

        fixture.Index.Flush(400);

        Assert.Equal(300, fixture.Lookup(1).Count);
        Assert.Equal(100, fixture.Lookup(2).Count);
    }

    [Fact]
    public async Task Maintain_DeclinesAMergeThatWouldNotReduceTheSegmentCount()
    {
        // MaxSegmentEntries equal to MemTableEntries means a merge of `fanout` full segments splits
        // back into `fanout` outputs: same segment count, one level up, every entry rewritten. Left
        // unchecked the cascade climbs a level per round and never terminates.
        using Fixture fixture = new(memTableEntries: 1024, maxSegmentEntries: 1024);

        for (ulong flush = 0; flush < 12; flush++)
        {
            for (ulong i = 0; i < 1024; i++)
            {
                fixture.Index.Add(i, (flush * 1024) + i);
            }

            fixture.Index.Flush((flush + 1) * 1024);
        }

        Assert.Equal(12, fixture.Index.SegmentCount);
        Assert.Equal(0UL, fixture.Index.PlannedMergeEntries());

        // A regression here is a hang, not an exception, so bound it: race the merge against a timer
        // and fail on the timer rather than letting the whole test run stall.
        Task merge = Task.Run(() => fixture.Index.Maintain());
        Assert.True(
            await Task.WhenAny(merge, Task.Delay(TimeSpan.FromSeconds(30))) == merge,
            "Maintain did not terminate: a merge that cannot reduce the segment count was accepted.");
        await merge;

        Assert.Equal(12, fixture.Index.SegmentCount);
        Assert.Equal(12, fixture.Lookup(0).Count);
    }

    [Fact]
    public async Task Maintain_MergesTheCascadeThenStopsAtTheSegmentCap()
    {
        // The realistic shape: merging pays off at the bottom and stops paying higher up. With a
        // fanout of three, a 1,024-entry memtable and a 4,096-entry cap, level 0 collapses three at a
        // time into 3,072-entry runs, but merging three of *those* yields 9,216 entries, which the cap
        // splits back into three segments. That merge is refused, and the cascade ends there instead
        // of climbing levels forever.
        using Fixture fixture = new(memTableEntries: 1024, maxSegmentEntries: 4096);

        for (ulong flush = 0; flush < 9; flush++)
        {
            for (ulong i = 0; i < 1024; i++)
            {
                fixture.Index.Add(i, (flush * 1024) + i);
            }

            fixture.Index.Flush((flush + 1) * 1024);
        }

        Assert.Equal(9, fixture.Index.SegmentCount);

        // Three level-0 merges of 3,072 entries each; the level-1 merge is declined.
        Assert.Equal(9216UL, fixture.Index.PlannedMergeEntries());

        Task merge = Task.Run(() => fixture.Index.Maintain());
        Assert.True(
            await Task.WhenAny(merge, Task.Delay(TimeSpan.FromSeconds(30))) == merge,
            "Maintain did not terminate.");
        await merge;

        Assert.Equal(3, fixture.Index.SegmentCount);
        Assert.Equal(0UL, fixture.Index.PlannedMergeEntries());

        // Every entry survived the cascade.
        Assert.Equal(9, fixture.Lookup(0).Count);
        Assert.Equal(9, fixture.Lookup(1023).Count);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _dir = new();
        private readonly BlockCache _cache;

        internal Fixture(int memTableEntries = 100_000, ulong? maxSegmentEntries = null)
        {
            StoreOptions options = TestData.SmallOptions(memTableEntries, maxSegmentEntries);
            _cache = new BlockCache(options.BlockSize, options.CacheBudgetBytes);
            Index = new SortedIndex(Path.Combine(_dir.Path, "index-test"), options, _cache);
        }

        internal SortedIndex Index { get; }

        internal List<ulong> Lookup(ulong key)
        {
            List<ulong> results = [];
            Index.Lookup(key, results);
            results.Sort();
            return results;
        }

        public void Dispose()
        {
            Index.Dispose();
            _cache.Dispose();
            _dir.Dispose();
        }
    }
}
