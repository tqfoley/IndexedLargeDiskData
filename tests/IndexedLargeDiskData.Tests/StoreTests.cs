using System.Text.Json;
using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Tests;

public class TransactionStoreTests
{
    [Fact]
    public void FindByV0_ReturnsEveryRecordSharingTheKey()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.Transactions.Append(1, 10, 100, 1000, 10000);
        root.Transactions.Append(2, 20, 200, 2000, 20000);
        root.Transactions.Append(1, 30, 300, 3000, 30000);

        IReadOnlyList<QuadrupleRecord> matches = root.Transactions.FindByV0(1);

        Assert.Equal(2, matches.Count);
        Assert.Equal(new QuadrupleRecord(1, 10, 100, 1000, 10000), matches[0]);
        Assert.Equal(new QuadrupleRecord(1, 30, 300, 3000, 30000), matches[1]);
    }

    [Fact]
    public void EveryIndexIsQueriedIndependently()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.Transactions.Append(7, 7, 1, 5, 7);
        root.Transactions.Append(7, 8, 2, 5, 7);
        root.Transactions.Append(9, 7, 3, 6, 8);

        Assert.Equal(2, root.Transactions.FindByV0(7).Count);
        Assert.Equal(2, root.Transactions.FindByV1(7).Count);
        Assert.Single(root.Transactions.FindByV1(8));
        Assert.Single(root.Transactions.FindByV2(2));
        Assert.Equal(2, root.Transactions.FindByV3(5).Count);
    }

    [Fact]
    public void AllFourValuesAreSearchableAcrossAFlushAndAReopen()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();

        using (DataRoot root = new(dir.Path, options))
        {
            for (ulong i = 0; i < 2000; i++)
            {
                root.Transactions.Append(i % 25, i % 17, i % 13, i % 9, i);
            }
        }

        // The third and fourth values carry their own indexes now, so a query can start from any
        // field rather than only from the first two.
        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(80, reopened.Transactions.FindByV0(3).Count);
        Assert.Equal(118, reopened.Transactions.FindByV1(3).Count);
        Assert.Equal(154, reopened.Transactions.FindByV2(3).Count);
        Assert.Equal(222, reopened.Transactions.FindByV3(3).Count);

        Assert.Equal(154UL, reopened.Transactions.CountByV2(3));
        Assert.Equal(222UL, reopened.Transactions.CountByV3(3));

        // Ordinals come back ascending, and each one really does carry the key it was found under.
        IReadOnlyList<ulong> ordinals = reopened.Transactions.FindOrdinalsByV3(3);
        Assert.Equal(222, ordinals.Count);
        Assert.Equal(ordinals.Order(), ordinals);
        Assert.All(ordinals, o => Assert.Equal(3UL, reopened.Transactions.Read(o).V3));

        // The fifth value rode along on the record: it is returned, and it is not a key.
        Assert.All(ordinals, o => Assert.Equal(o, reopened.Transactions.Read(o).V4));
    }

    [Fact]
    public void TheFifthValueIsStoredButNeverIndexed()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();

        using (DataRoot root = new(dir.Path, options))
        {
            root.Transactions.Append(1, 2, 3, 4, 5);
            root.Transactions.Append(6, 7, 8, 9, 5);
        }

        using DataRoot reopened = new(dir.Path, options);

        // Four index directories for five fields: the payload has none, so a value that only ever
        // appears in V4 cannot be reached through any of them.
        Assert.Equal(4, reopened.Transactions.GetIndexStatistics().Count);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "transactions", "index-v4")));

        Assert.Equal(5UL, reopened.Transactions.Read(0).V4);
        Assert.Equal(5UL, reopened.Transactions.Read(1).V4);
        Assert.Empty(reopened.Transactions.FindByV0(5));
        Assert.Empty(reopened.Transactions.FindByV3(5));
    }

    [Fact]
    public void LookupsAgreeWithAnInMemoryModelAcrossFlushesAndMerges()
    {
        using TempDirectory dir = new();
        Dictionary<ulong, List<QuadrupleRecord>> byV0 = [];
        Dictionary<ulong, List<QuadrupleRecord>> byV1 = [];

        using DataRoot root = new(dir.Path, TestData.SmallOptions(memTableEntries: 1024));

        ulong seed = 20250825;
        for (ulong i = 0; i < 6000; i++)
        {
            ulong v0 = TestData.Next(ref seed) % 40;
            ulong v1 = TestData.Next(ref seed) % 40;
            QuadrupleRecord record = new(v0, v1, i, i % 7, i * 2);

            root.Transactions.Append(record);
            TestData.Track(byV0, v0, record);
            TestData.Track(byV1, v1, record);
        }

        root.Flush();
        root.Maintain();

        // The memtable holds 1024 entries, so 6000 records forced several flushes and merges.
        Assert.True(root.Transactions.GetIndexStatistics()[0].SegmentCount >= 1);

        for (ulong key = 0; key < 40; key++)
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
            for (ulong i = 0; i < 2000; i++)
            {
                root.Transactions.Append(i % 25, i % 17, i, i, i);
            }
        }

        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(2000UL, reopened.Transactions.Count);
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
            for (ulong i = 0; i < 500; i++)
            {
                root.Transactions.Append(i % 11, i, i, i, i);
            }
        }

        // Records are the source of truth; an index is derived state that can simply be deleted.
        string indexDirectory = Path.Combine(dir.Path, "transactions", "index-v0");
        foreach (string file in Directory.GetFiles(indexDirectory))
        {
            File.Delete(file);
        }

        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(500UL, reopened.Transactions.Count);
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
            root.Transactions.Append(1, 2, 3, 4, 5);
            root.Flush();
        }

        // Bytes that reached the disk but were never committed: an index could never reference
        // them, so opening the store has to drop them rather than expose dangling ordinals.
        string dataDirectory = Path.Combine(dir.Path, "transactions", "data");
        string last = Directory.GetFiles(dataDirectory, "*.dat").Order().Last();
        using (FileStream stream = new(last, FileMode.Append))
        {
            stream.Write(new byte[QuadrupleRecord.RecordSize * 3]);
        }

        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(1UL, reopened.Transactions.Count);
        Assert.Equal(new QuadrupleRecord(1, 2, 3, 4, 5), reopened.Transactions.Read(0));
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

        Assert.True(root.Addresses.TryGetId(address, out ulong id));
        Assert.Equal(500UL, id);
    }

    [Fact]
    public void ReturnsTheAddressTextUnchanged()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        string text = TestData.Address(9);
        root.Addresses.Append(77, text);

        Assert.True(root.Addresses.TryGetId(text, out ulong id));
        Assert.Equal(77UL, id);
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

        Assert.True(root.Addresses.TryGetId(first, out ulong firstId));
        Assert.True(root.Addresses.TryGetId(second, out ulong secondId));
        Assert.Equal(10UL, firstId);
        Assert.Equal(20UL, secondId);

        Assert.Single(root.Addresses.FindByAddress(first));
        Assert.Empty(root.Addresses.FindByAddress(TestData.AddressWithPrefix("deadbeef", '3')));
    }

    [Fact]
    public void ScalesPastAFlushAndAReopen()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        const ulong count = 3000;

        using (DataRoot root = new(dir.Path, options))
        {
            for (ulong id = 0; id < count; id++)
            {
                root.Addresses.Append(id, TestData.Address(id));
            }
        }

        using DataRoot reopened = new(dir.Path, options);

        Assert.Equal(count, reopened.Addresses.Count);
        for (ulong id = 0; id < count; id += 97)
        {
            Assert.True(reopened.Addresses.TryGetAddress(id, out string? address));
            Assert.Equal(TestData.Address(id), address);

            Assert.True(reopened.Addresses.TryGetId(TestData.Address(id), out ulong found));
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
        Assert.True(Directory.Exists(Path.Combine(root.Path, "transactions", "index-v2")));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "transactions", "index-v3")));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "addresses", "index-id")));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "addresses", "index-address")));
    }

    [Fact]
    public void SharesOneCacheAcrossEveryStore()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.Transactions.Append(1, 2, 3, 4, 5);
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

    [Fact]
    public void RecordsItsOptionsBesideTheStores()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();

        using (DataRoot root = new(dir.Path, options))
        {
            Assert.True(File.Exists(Path.Combine(root.Path, "options.json")));
        }

        string json = File.ReadAllText(Path.Combine(dir.Path, "options.json"));
        StoreOptions? stored = JsonSerializer.Deserialize<StoreOptions>(json);

        Assert.NotNull(stored);
        Assert.Equal(options.BlockSize, stored.BlockSize);
        Assert.Equal(options.SegmentSize, stored.SegmentSize);
        Assert.Equal(options.MaxSegmentEntries, stored.MaxSegmentEntries);
    }

    [Fact]
    public void ReopensWithTheOptionsItWasWrittenWith()
    {
        using TempDirectory dir = new();

        using (DataRoot root = new(dir.Path, TestData.SmallOptions()))
        {
            root.Transactions.Append(1, 2, 3, 4, 5);
        }

        // A separate instance of the same values, so the check compares contents rather than identity.
        using DataRoot reopened = new(dir.Path, TestData.SmallOptions());
        Assert.Equal(1UL, reopened.Transactions.Count);
    }

    [Fact]
    public void RejectsAReopenWithDifferentOptions()
    {
        using TempDirectory dir = new();

        using (DataRoot root = new(dir.Path, TestData.SmallOptions()))
        {
            root.Transactions.Append(1, 2, 3, 4, 5);
        }

        // The failure this prevents: data written under one set of sizes and read back under another
        // surfaces down in the storage layer as a truncated segment or an index that will not parse,
        // with nothing in the complaint pointing at the option that actually moved.
        StoreOptions different = TestData.SmallOptions(maxSegmentEntries: 2048);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new DataRoot(dir.Path, different));
        Assert.Contains(nameof(StoreOptions.MaxSegmentEntries), error.Message);
    }

    [Fact]
    public void RejectsAnUnreadableOptionsFile()
    {
        using TempDirectory dir = new();

        using (new DataRoot(dir.Path, TestData.SmallOptions()))
        {
        }

        File.WriteAllText(Path.Combine(dir.Path, "options.json"), "{ not json");

        Assert.Throws<InvalidDataException>(() => new DataRoot(dir.Path, TestData.SmallOptions()));
    }
}
