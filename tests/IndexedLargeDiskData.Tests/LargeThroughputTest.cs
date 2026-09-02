using System.Diagnostics;
using IndexedLargeDiskData.Records;
using IndexedLargeDiskData.Stores;
using Xunit.Abstractions;

namespace IndexedLargeDiskData.Tests;

[Trait("Category", "Throughput")]
public class LargeThroughputTest(ITestOutputHelper output)
{
    /// <summary>
    /// Where the throughput runs write. A fixed location rather than the temp drive, so the data is
    /// still there to look at afterwards and a run can be aimed at a particular disk.
    /// </summary>
    private const string RootPath = @"c:\unittest\abvbabcb";

    private const ulong TransactionCount = 20_000_000;
    private const int AddressCount = 300_000;
    private const int DistinctKeys = 2_000;
    private const int BatchSize = 8192;
    private const int SampleStride = 97;

    /// <summary>Gives one test an empty directory of its own under <see cref="RootPath"/>.</summary>
    /// <remarks>
    /// Two things go wrong when these tests share a single directory, and both did. The stores are
    /// append-only, so a second run finds the first run's records still sitting there and every exact
    /// count assertion fails by a multiple. And the tests ask for different
    /// <see cref="StoreOptions.SegmentSize"/> values, which <c>options.json</c> refuses on the reopen.
    /// A directory per test, emptied on the way in, is what makes a run repeatable.
    /// </remarks>
    private static string FreshDirectory(string name)
    {
        string directory = System.IO.Path.Combine(RootPath, name);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    /*[Fact]
    public void TransactionLookupsMatchTheModelAtVolume()
    {
        string path = FreshDirectory("transactions");
        output.WriteLine($"data    {path}");

        // 20 million * 5 * 8 bytes = 800 million 

        StoreOptions options = new StoreOptions()
        {
            BlockSize = 4096,
            CacheBudgetBytes = 64L * 1024 * 1024,
            SegmentSize = 50L * 1024 * 1024, // 
            MemTableEntries = 1 << 16,
            MaxSegmentEntries = 1 << 20,
            MergeFanout = 4,
            FenceStride = 4096,
            BloomBitsPerKey = 10,
            WriteBufferBytes = 1 << 20,
        };

        using DataRoot root = new(path, options);

        Dictionary<long, List<QuadrupleRecord>> byV0 = [];
        Dictionary<long, List<QuadrupleRecord>> byV1 = [];

        QuadrupleRecord[] batch = new QuadrupleRecord[BatchSize];
        ulong seed = 20250825;
        long written = 0;

        Stopwatch clock = Stopwatch.StartNew();
        while (written < TransactionCount)
        {
            long take = Math.Min(((long)batch.Length), TransactionCount - written);
            for (int i = 0; i < take; i++)
            {
                long v0 = (long)(TestData.Next(ref seed) % DistinctKeys);
                long v1 = (long)(TestData.Next(ref seed) % DistinctKeys);
                batch[i] = new QuadrupleRecord(v0, v1, written + i, v0 + v1, v0 * v1);

                TestData.Track(byV0, v0, batch[i]);
                TestData.Track(byV1, v1, batch[i]);
            }

            if(take > 1000000000)
            {
                throw new Exception("overflow");
            }
            root.Transactions.AppendRange(batch.AsSpan(0, (int)take));
            written += take;
        }

        root.Flush();
        Report("ingest", TransactionCount, clock);

        int segmentsBefore = root.Transactions.GetIndexStatistics()[0].SegmentCount;
        clock.Restart();
        root.Maintain();
        int segmentsAfter = root.Transactions.GetIndexStatistics()[0].SegmentCount;
        output.WriteLine($"merge   {segmentsBefore} -> {segmentsAfter} segments in " +
                         $"{clock.Elapsed.TotalSeconds:F2}s  [{Describe(root.Transactions)}]");

        // A memtable of 65,536 entries against 250,000 records: the flush and merge paths ran for
        // real, which is the whole reason this test exists at this volume.
        Assert.True(segmentsBefore > 1, $"expected several level-0 flushes, saw {segmentsBefore}");
        Assert.True(segmentsAfter < segmentsBefore, $"expected a merge, {segmentsBefore} -> {segmentsAfter}");
        Assert.Equal(TransactionCount, root.Transactions.Count);

        clock.Restart();
        for (long key = 0; key < DistinctKeys; key+=64)
        {
            if (key < DistinctKeys)
            {
                Assert.Equal(byV0.GetValueOrDefault(key, []), root.Transactions.FindByV0(key));
                Assert.Equal(byV1.GetValueOrDefault(key, []), root.Transactions.FindByV1(key));

            }
        }

            Report("verify", DistinctKeys * 2, clock, "lookups");
        output.WriteLine(CacheLine(root));
    }*/

    [Fact]
    public void AddressesRoundTripInBothDirectionsAtVolume()
    {

        StoreOptions options = new StoreOptions()
        {
            BlockSize = 4096,
            CacheBudgetBytes = 64L * 1024 * 1024,
            SegmentSize = 16L * 1024 * 1024,
            MemTableEntries = 1 << 16,
            MaxSegmentEntries = 1 << 20,
            MergeFanout = 4,
            FenceStride = 4096,
            BloomBitsPerKey = 10,
            WriteBufferBytes = 1 << 20,
        };

        string path = FreshDirectory("addresses");
        using DataRoot root = new(path, options);

        Dictionary<ulong, List<AddressRecord>> byId = [];
        AddressRecord[] batch = new AddressRecord[BatchSize];
        int written = 0;

        Stopwatch clock = Stopwatch.StartNew();
        while (written < AddressCount)
        {
            int take = Math.Min(batch.Length, AddressCount - written);
            for (int i = 0; i < take; i++)
            {
                ulong id = (ulong)(written + i);
                batch[i] = new AddressRecord(id, TestData.Address(id));
                TestData.Track(byId, id, batch[i]);
            }

            root.Addresses.AppendRange(batch.AsSpan(0, take));
            written += take;
        }

        root.Flush();
        Report("ingest", AddressCount, clock);

        clock.Restart();
        root.Maintain();
        output.WriteLine($"merge   {clock.Elapsed.TotalSeconds:F2}s  [{Describe(root.Addresses)}]");

        Assert.Equal((ulong)AddressCount, root.Addresses.Count);

        // Sampled rather than exhaustive: the reverse lookup reads a record per prefix candidate to
        // confirm the full 75 characters, so checking all 300,000 would dominate the suite's runtime.
        int probes = 0;
        clock.Restart();
        for (ulong id = 0; id < AddressCount; id += SampleStride)
        {
            Assert.Equal(byId[id], root.Addresses.FindById(id));

            Assert.True(root.Addresses.TryGetId(TestData.Address(id), out ulong found));
            Assert.Equal(id, found);
            probes++;
        }

        Report("verify", (ulong)(probes * 2), clock, "lookups");
        output.WriteLine(CacheLine(root));
    }

    /// <summary>
    /// Writes, closes, reopens, writes more, closes, reopens, then queries across both sessions.
    /// </summary>
    /// <remarks>
    /// The reopen in the middle is the part worth testing. A store that is closed and reopened has to
    /// pick the record count back up from the manifest and replay each index from its own
    /// <c>CoveredUpTo</c>, so the second session's appends land on ordinals that continue from the
    /// first. If any of that is off, the failure is not a crash: lookups quietly return the first
    /// session's records and silently drop the second's, which is why the final queries assert that a
    /// key matches records from *both* sessions rather than merely returning something.
    /// </remarks>
    /*[Fact]
    public void RecordsSurviveCloseReopenFurtherAppendsAndReopen()
    {
        const int firstSession = 1_000_000;
        const int secondSession = 1_000_000;
        const int addressesPerSession = 50_000;

        // Emptied on the way in: this test is about reopen semantics, and records left by an earlier
        // run would make the count assertions fail for the wrong reason.
        string path = FreshDirectory("reopen");

        StoreOptions options = new StoreOptions()
        {
            BlockSize = 4096,
            CacheBudgetBytes = 64L * 1024 * 1024,
            SegmentSize = 50L * 1024 * 1024, 
            MemTableEntries = 1 << 16,
            MaxSegmentEntries = 1 << 20,
            MergeFanout = 4,
            FenceStride = 4096,
            BloomBitsPerKey = 10,
            WriteBufferBytes = 1 << 20,
        };


        Dictionary<long, List<QuadrupleRecord>> byV0 = [];
        Dictionary<long, List<QuadrupleRecord>> byV1 = [];
        ulong seed = 20250825;

        Stopwatch clock = Stopwatch.StartNew();

        // Session one: write, commit to disk, close the data files.
        using (DataRoot root = new(path, options))
        {
            AppendTransactions(root, byV0, byV1, ref seed, firstOrdinal: 0, count: firstSession);
            AppendAddresses(root, firstId: 0, count: addressesPerSession);

            root.Flush();

            Assert.Equal(firstSession, root.Transactions.Count);
            Assert.Equal(firstSession, root.Transactions.CommittedCount);
        }

        output.WriteLine($"session 1  wrote {firstSession:N0} transactions, {addressesPerSession:N0} addresses");

        // Session two: reopen, confirm the first session is intact, append more, commit, close.
        using (DataRoot root = new(path, options))
        {
            Assert.Equal(firstSession, root.Transactions.Count);
            Assert.Equal(addressesPerSession, root.Addresses.Count);

            // Readable straight after reopen, before anything new is written: proves the index was
            // rebuilt from the records rather than needing the previous process still to be alive.
            Assert.NotEmpty(root.Transactions.FindByV0(0));

            AppendTransactions(root, byV0, byV1, ref seed, firstOrdinal: firstSession, count: secondSession);
            AppendAddresses(root, firstId: addressesPerSession, count: addressesPerSession);

            root.Flush();
            root.Maintain();

            Assert.Equal(firstSession + secondSession, root.Transactions.CommittedCount);
        }

        output.WriteLine($"session 2  wrote {secondSession:N0} transactions, {addressesPerSession:N0} addresses");

        // Session three: reopen once more and query only.
        using (DataRoot root = new(path, options))
        {
            Assert.Equal(firstSession + secondSession, root.Transactions.Count);
            Assert.Equal(addressesPerSession * 2, root.Addresses.Count);

            clock.Restart();
            int spanningKeys = 0;
            for (long key = 0; key < DistinctKeys; key++)
            {
                IReadOnlyList<QuadrupleRecord> matches = root.Transactions.FindByV0(key);
                Assert.Equal(byV0.GetValueOrDefault(key, []), matches);
                Assert.Equal(byV1.GetValueOrDefault(key, []), root.Transactions.FindByV1(key));

                // V2 carries the ordinal, so it says which session wrote the record.
                if (matches.Any(r => r.V2 < firstSession) && matches.Any(r => r.V2 >= firstSession))
                {
                    spanningKeys++;
                }
            }

            Assert.True(
                spanningKeys > DistinctKeys / 2,
                $"only {spanningKeys} of {DistinctKeys} keys matched records from both sessions");

            // Both stores, both directions, across the session boundary.
            for (long id = 0; id < addressesPerSession * 2; id += SampleStride)
            {
                Assert.True(root.Addresses.TryGetAddress(id, out string? address));
                Assert.Equal(TestData.Address(id), address);
                Assert.True(root.Addresses.TryGetId(address, out long found));
                Assert.Equal(id, found);
            }

            Report("verify", DistinctKeys * 2, clock, "lookups");
            output.WriteLine($"session 3  {spanningKeys:N0}/{DistinctKeys:N0} keys span both sessions");
            output.WriteLine(CacheLine(root));
        }
    }*/

    private void AppendTransactions(
        DataRoot root,
        Dictionary<ulong, List<QuadrupleRecord>> byV0,
        Dictionary<ulong, List<QuadrupleRecord>> byV1,
        ref ulong seed,
        ulong firstOrdinal,
        int count)
    {
        QuadrupleRecord[] batch = new QuadrupleRecord[BatchSize];
        int written = 0;

        while (written < count)
        {
            int take = Math.Min(batch.Length, count - written);
            for (int i = 0; i < take; i++)
            {
                ulong v0 = (ulong)(TestData.Next(ref seed) % DistinctKeys);
                ulong v1 = (ulong)(TestData.Next(ref seed) % DistinctKeys);
                batch[i] = new QuadrupleRecord(v0, v1, firstOrdinal + (ulong)(written + i), v0 + v1, v0 * v1);

                TestData.Track(byV0, v0, batch[i]);
                TestData.Track(byV1, v1, batch[i]);
            }

            root.Transactions.AppendRange(batch.AsSpan(0, take));
            written += take;
        }
    }

    private static void AppendAddresses(DataRoot root, ulong firstId, int count)
    {
        AddressRecord[] batch = new AddressRecord[BatchSize];
        int written = 0;

        while (written < count)
        {
            int take = Math.Min(batch.Length, count - written);
            for (int i = 0; i < take; i++)
            {
                ulong id = firstId + (ulong)(written + i);
                batch[i] = new AddressRecord(id, TestData.Address(id));
            }

            root.Addresses.AppendRange(batch.AsSpan(0, take));
            written += take;
        }
    }

    private void Report(string phase, ulong items, Stopwatch clock, string unit = "records")
    {
        double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.0001);
        output.WriteLine($"{phase,-7} {items,9:N0} {unit} in {seconds,6:F2}s  ({items / seconds,11:N0} {unit}/s)");
    }

    private static string Describe<T>(IndexedStore<T> store)
        where T : IFixedRecord<T> =>
        string.Join(", ", store.GetIndexStatistics().Select(s => $"{s.Name}:{s.SegmentCount}"));

    private static string CacheLine(DataRoot root)
    {
        ulong total = root.Cache.Hits + root.Cache.Misses;
        double hitRate;
        if (total == 0)
        {
            hitRate = 0;
        }
        else
        {
            hitRate = root.Cache.Hits * 100.0 / total;
        }
        return $"cache   {root.Cache.Hits:N0} hits / {root.Cache.Misses:N0} misses ({hitRate:F1}% hit), " +
               $"{root.Cache.Evictions:N0} evictions, " +
               $"{root.Cache.Resident:N0}/{root.Cache.Capacity:N0} blocks resident";
    }
}
