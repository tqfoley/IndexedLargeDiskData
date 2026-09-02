using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Records;
using IndexedLargeDiskData.Storage;

namespace IndexedLargeDiskData.Tests;

public class RecordStoreTests
{
    [Fact]
    public void Append_AssignsConsecutiveOrdinals()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        using BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<QuadrupleRecord> store = new(Path.Combine(dir.Path, "data"), options, cache);

        for (ulong i = 0; i < 100; i++)
        {
            Assert.Equal(i, store.Append(new QuadrupleRecord(i, i * 2, i * 3, i * 4, i * 5)));
        }

        Assert.Equal(100UL, store.Count);
    }

    [Fact]
    public void Read_SeesRecordsStillInTheWriteBuffer()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        using BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<QuadrupleRecord> store = new(Path.Combine(dir.Path, "data"), options, cache);

        ulong ordinal = store.Append(new QuadrupleRecord(11, 22, 33, 44, 55));

        // No flush: the record only exists in memory at this point.
        Assert.Equal(new QuadrupleRecord(11, 22, 33, 44, 55), store.Read(ordinal));
    }

    [Fact]
    public void Records_SurviveSegmentRolloverAndReopen()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        string path = Path.Combine(dir.Path, "data");
        const ulong count = 1000;

        using (BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes))
        using (RecordStore<QuadrupleRecord> store = new(path, options, cache))
        {
            for (ulong i = 0; i < count; i++)
            {
                store.Append(new QuadrupleRecord(i, ulong.MaxValue - i, (ulong.MaxValue / 2) + i, i * 2, i * 3));
            }

            store.Flush();
        }

        // 4080 usable bytes per segment at 40 bytes a record: this must have rolled over.
        Assert.True(Directory.GetFiles(path, "*.dat").Length > 1);

        using BlockCache reopenedCache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<QuadrupleRecord> reopened = new(path, options, reopenedCache);

        Assert.Equal(count, reopened.Count);
        for (ulong i = 0; i < count; i++)
        {
            Assert.Equal(
                new QuadrupleRecord(i, ulong.MaxValue - i, (ulong.MaxValue / 2) + i, i * 2, i * 3),
                reopened.Read(i));
        }
    }

    [Fact]
    public void ReadRange_ReturnsAContiguousRun()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        using BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<QuadrupleRecord> store = new(Path.Combine(dir.Path, "data"), options, cache);

        for (ulong i = 0; i < 500; i++)
        {
            store.Append(new QuadrupleRecord(i, i, i, i, i));
        }

        store.Flush();

        QuadrupleRecord[] window = new QuadrupleRecord[50];
        store.ReadRange(200, window);

        for (ulong i = 0; i < (ulong)window.Length; i++)
        {
            Assert.Equal(new QuadrupleRecord(200 + i, 200 + i, 200 + i, 200 + i, 200 + i), window[i]);
        }
    }

    [Fact]
    public void AppendRange_MatchesRepeatedAppend()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        using BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<AddressRecord> store = new(Path.Combine(dir.Path, "data"), options, cache);

        AddressRecord[] batch =
            [.. Enumerable.Range(0, 300).Select(i => new AddressRecord((ulong)i, TestData.Address((ulong)i)))];
        Assert.Equal(0UL, store.AppendRange(batch));
        store.Flush();

        Assert.Equal((ulong)batch.Length, store.Count);
        for (ulong i = 0; i < (ulong)batch.Length; i++)
        {
            Assert.Equal(batch[i], store.Read(i));
        }
    }

    [Fact]
    public void Open_DropsATornTrailingRecord()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        string path = Path.Combine(dir.Path, "data");

        using (BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes))
        using (RecordStore<QuadrupleRecord> store = new(path, options, cache))
        {
            store.Append(new QuadrupleRecord(1, 2, 3, 4, 5));
            store.Flush();
        }

        // Simulate a process death midway through writing record two.
        string last = Directory.GetFiles(path, "*.dat").Order().Last();
        using (FileStream stream = new(last, FileMode.Append))
        {
            stream.Write(new byte[7]);
        }

        using BlockCache reopenedCache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<QuadrupleRecord> reopened = new(path, options, reopenedCache);

        Assert.Equal(1UL, reopened.Count);
        Assert.Equal(new QuadrupleRecord(1, 2, 3, 4, 5), reopened.Read(0));
    }
}
