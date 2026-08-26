using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Tests;

public class TransactionStoreTests
{
    [Fact]
    public void FindByV0_ReturnsEveryRecordSharingTheKey()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.Transactions.Append(1, 10, 100);
        root.Transactions.Append(2, 20, 200);
        root.Transactions.Append(1, 30, 300);

        IReadOnlyList<TripleRecord> matches = root.Transactions.FindByV0(1);

        Assert.Equal(2, matches.Count);
        Assert.Equal(new TripleRecord(1, 10, 100), matches[0]);
        Assert.Equal(new TripleRecord(1, 30, 300), matches[1]);
    }

    [Fact]
    public void BothIndexesAreQueriedIndependently()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.Transactions.Append(7, 7, 1);
        root.Transactions.Append(7, 8, 2);
        root.Transactions.Append(9, 7, 3);

        Assert.Equal(2, root.Transactions.FindByV0(7).Count);
        Assert.Equal(2, root.Transactions.FindByV1(7).Count);
        Assert.Single(root.Transactions.FindByV1(8));
    }

    [Fact]
    public void LookupsAgreeWithAnInMemoryModelAcrossFlushesAndMerges()
    {
        using TempDirectory dir = new();
        Dictionary<long, List<TripleRecord>> byV0 = [];
        Dictionary<long, List<TripleRecord>> byV1 = [];

        using DataRoot root = new(dir.Path, TestData.SmallOptions(memTableEntries: 1024));

        ulong seed = 20250825;
        for (int i = 0; i < 6000; i++)
        {
            long v0 = (long)(TestData.Next(ref seed) % 40);
            long v1 = (long)(TestData.Next(ref seed) % 40);
            TripleRecord record = new(v0, v1, i);

            root.Transactions.Append(record);
            TestData.Track(byV0, v0, record);
            TestData.Track(byV1, v1, record);
        }

        root.Flush();
        root.Maintain();

        // The memtable holds 1024 entries, so 6000 records forced several flushes and merges.
        Assert.True(root.Transactions.GetIndexStatistics()[0].SegmentCount >= 1);

        for (long key = 0; key < 40; key++)
        {
            Assert.Equal(byV0.GetValueOrDefault(key, []), root.Transactions.FindByV0(key));
            Assert.Equal(byV1.GetValueOrDefault(key, []), root.Transactions.FindByV1(key));
        }
    }

    [Fact]
    public void DataAndIndexesSurviveAReopen()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();

        using (DataRoot root = new(dir.Path, options))
        {
            for (int i = 0; i < 2000; i++)
            {
                root.Transactions.Append(i % 25, i % 17, i);
            }
        }

        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(2000, reopened.Transactions.Count);
        Assert.Equal(80, reopened.Transactions.FindByV0(3).Count);
        Assert.Equal(118, reopened.Transactions.FindByV1(3).Count);
    }

    [Fact]
    public void IndexesRebuildFromTheRecordsWhenTheirSegmentsAreLost()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();

        using (DataRoot root = new(dir.Path, options))
        {
            for (int i = 0; i < 500; i++)
            {
                root.Transactions.Append(i % 11, i, i);
            }
        }

        // Records are the source of truth; an index is derived state that can simply be deleted.
        string indexDirectory = Path.Combine(dir.Path, "transactions", "index-v0");
        foreach (string file in Directory.GetFiles(indexDirectory))
        {
            File.Delete(file);
        }

        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(500, reopened.Transactions.Count);
        Assert.Equal(46, reopened.Transactions.FindByV0(0).Count);
        Assert.Equal(45, reopened.Transactions.FindByV0(10).Count);
    }

    [Fact]
    public void RecordsWrittenPastTheCommittedCountAreDiscardedOnOpen()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();

        using (DataRoot root = new(dir.Path, options))
        {
            root.Transactions.Append(1, 2, 3);
            root.Flush();
        }

        // Bytes that reached the disk but were never committed: an index could never reference
        // them, so opening the store has to drop them rather than expose dangling ordinals.
        string dataDirectory = Path.Combine(dir.Path, "transactions", "data");
        string last = Directory.GetFiles(dataDirectory, "*.dat").Order().Last();
        using (FileStream stream = new(last, FileMode.Append))
        {
            stream.Write(new byte[TripleRecord.RecordSize * 3]);
        }

        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(1, reopened.Transactions.Count);
        Assert.Equal(new TripleRecord(1, 2, 3), reopened.Transactions.Read(0));
    }
}

public class AddressStoreTests
{
    [Fact]
    public void LooksUpInBothDirections()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        string address = TestData.Address(1);
        root.Addresses.Append(500, address);
        root.Addresses.Append(501, TestData.Address(2));

        Assert.True(root.Addresses.TryGetAddress(500, out string? found));
        Assert.Equal(address, found);

        Assert.True(root.Addresses.TryGetId(address, out long id));
        Assert.Equal(500, id);
    }

    [Fact]
    public void ReturnsTheAddressTextUnchanged()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        string text = TestData.Address(9);
        root.Addresses.Append(77, text);

        Assert.True(root.Addresses.TryGetId(text, out long id));
        Assert.Equal(77, id);
        Assert.True(root.Addresses.TryGetAddress(77, out string? address));
        Assert.Equal(text, address);
        Assert.Equal(AddressRecord.AddressLength, address.Length);
    }

    [Fact]
    public void MissingKeysReturnFalse()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.Addresses.Append(1, TestData.Address(1));

        Assert.False(root.Addresses.TryGetAddress(2, out _));
        Assert.False(root.Addresses.TryGetId(TestData.Address(2), out _));
    }

    [Fact]
    public void AddressesSharingAnIndexPrefixAreToldApart()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        // Both addresses open with the same eight characters, so they share one index key and the
        // index alone cannot separate them; the store has to confirm each candidate against the full
        // 75 characters on the record.
        string first = TestData.AddressWithPrefix("deadbeef", '1');
        string second = TestData.AddressWithPrefix("deadbeef", '2');
        Assert.Equal(AddressRecord.PrefixOf(first), AddressRecord.PrefixOf(second));

        root.Addresses.Append(10, first);
        root.Addresses.Append(20, second);
        root.Flush();

        Assert.True(root.Addresses.TryGetId(first, out long firstId));
        Assert.True(root.Addresses.TryGetId(second, out long secondId));
        Assert.Equal(10, firstId);
        Assert.Equal(20, secondId);

        Assert.Single(root.Addresses.FindByAddress(first));
        Assert.Empty(root.Addresses.FindByAddress(TestData.AddressWithPrefix("deadbeef", '3')));
    }

    [Fact]
    public void ScalesPastAFlushAndAReopen()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        const int count = 3000;

        using (DataRoot root = new(dir.Path, options))
        {
            for (int id = 0; id < count; id++)
            {
                root.Addresses.Append(id, TestData.Address(id));
            }
        }

        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(count, reopened.Addresses.Count);
        for (int id = 0; id < count; id += 97)
        {
            Assert.True(reopened.Addresses.TryGetAddress(id, out string? address));
            Assert.Equal(TestData.Address(id), address);

            Assert.True(reopened.Addresses.TryGetId(TestData.Address(id), out long found));
            Assert.Equal(id, found);
        }
    }
}

public class DataRootTests
{
    [Fact]
    public void CreatesItsDirectoryLayout()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(Path.Combine(dir.Path, "nested", "root"), TestData.SmallOptions());

        Assert.True(Directory.Exists(Path.Combine(root.Path, "transactions", "data")));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "transactions", "index-v0")));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "transactions", "index-v1")));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "addresses", "index-id")));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "addresses", "index-address")));
    }

    [Fact]
    public void SharesOneCacheAcrossEveryStore()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.Transactions.Append(1, 2, 3);
        root.Addresses.Append(1, TestData.Address(1));
        root.Flush();

        root.Transactions.FindByV0(1);
        root.Addresses.TryGetAddress(1, out _);

        Assert.True(root.Cache.Hits + root.Cache.Misses > 0);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(0)]
    public void RejectsInvalidBlockSizes(int blockSize)
    {
        using TempDirectory dir = new();
        Assert.Throws<ArgumentException>(() => new DataRoot(dir.Path, new StoreOptions { BlockSize = blockSize }));
    }
}
