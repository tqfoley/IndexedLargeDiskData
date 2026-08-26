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
        using RecordStore<TripleRecord> store = new(Path.Combine(dir.Path, "data"), options, cache);

        for (long i = 0; i < 100; i++)
        {
            Assert.Equal(i, store.Append(new TripleRecord(i, i * 2, i * 3)));
        }

        Assert.Equal(100, store.Count);
    }

    [Fact]
    public void Read_SeesRecordsStillInTheWriteBuffer()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        using BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<TripleRecord> store = new(Path.Combine(dir.Path, "data"), options, cache);

        long ordinal = store.Append(new TripleRecord(11, 22, 33));

        // No flush: the record only exists in memory at this point.
        Assert.Equal(new TripleRecord(11, 22, 33), store.Read(ordinal));
    }

    [Fact]
    public void Records_SurviveSegmentRolloverAndReopen()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        string path = Path.Combine(dir.Path, "data");
        const int count = 1000;

        using (BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes))
        using (RecordStore<TripleRecord> store = new(path, options, cache))
        {
            for (int i = 0; i < count; i++)
            {
                store.Append(new TripleRecord(i, -i, long.MaxValue - i));
            }

            store.Flush();
        }

        // 4080 usable bytes per segment at 24 bytes a record: this must have rolled over.
        Assert.True(Directory.GetFiles(path, "*.dat").Length > 1);

        using BlockCache reopenedCache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<TripleRecord> reopened = new(path, options, reopenedCache);

        Assert.Equal(count, reopened.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(new TripleRecord(i, -i, long.MaxValue - i), reopened.Read(i));
        }
    }

    [Fact]
    public void ReadRange_ReturnsAContiguousRun()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        using BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<TripleRecord> store = new(Path.Combine(dir.Path, "data"), options, cache);

        for (int i = 0; i < 500; i++)
        {
            store.Append(new TripleRecord(i, i, i));
        }

        store.Flush();

        TripleRecord[] window = new TripleRecord[50];
        store.ReadRange(200, window);

        for (int i = 0; i < window.Length; i++)
        {
            Assert.Equal(new TripleRecord(200 + i, 200 + i, 200 + i), window[i]);
        }
    }

    [Fact]
    public void AppendRange_MatchesRepeatedAppend()
    {
        using TempDirectory dir = new();
        StoreOptions options = TestData.SmallOptions();
        using BlockCache cache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<AddressRecord> store = new(Path.Combine(dir.Path, "data"), options, cache);

        AddressRecord[] batch = [.. Enumerable.Range(0, 300).Select(i => new AddressRecord(i, TestData.Address(i)))];
        Assert.Equal(0, store.AppendRange(batch));
        store.Flush();

        Assert.Equal(batch.Length, store.Count);
        for (int i = 0; i < batch.Length; i++)
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
        using (RecordStore<TripleRecord> store = new(path, options, cache))
        {
            store.Append(new TripleRecord(1, 2, 3));
            store.Flush();
        }

        // Simulate a process death midway through writing record two.
        string last = Directory.GetFiles(path, "*.dat").Order().Last();
        using (FileStream stream = new(last, FileMode.Append))
        {
            stream.Write(new byte[7]);
        }

        using BlockCache reopenedCache = new(options.BlockSize, options.CacheBudgetBytes);
        using RecordStore<TripleRecord> reopened = new(path, options, reopenedCache);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(new TripleRecord(1, 2, 3), reopened.Read(0));
    }
}
