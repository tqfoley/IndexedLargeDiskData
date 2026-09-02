using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Tests;

/// <summary>A scratch directory that deletes itself at the end of a test.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ildd-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A handle lingering on a test failure should not mask the real assertion.
        }
    }
}

/// <summary>Helpers shared by the store tests.</summary>
public static class TestData
{
    /// <summary>Characters that generated addresses are drawn from.</summary>
    private const string AddressAlphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Options sized so that a few thousand records exercise segment rollover, cache eviction,
    /// memtable flushes and tier merges, all of which would otherwise need terabytes to reach.
    /// </summary>
    public static StoreOptions SmallOptions(int memTableEntries = 1024, ulong? maxSegmentEntries = null) => new()
    {
        BlockSize = 512,
        CacheBudgetBytes = 512 * 64,
        SegmentSize = 4096,
        MemTableEntries = memTableEntries,
        MaxSegmentEntries = maxSegmentEntries ?? ((ulong)memTableEntries * 8UL),
        MergeFanout = 3,
        FenceStride = 16,
        BloomBitsPerKey = 10,
        WriteBufferBytes = 512,
    };

    /// <summary>
    /// Production-shaped options, scaled down only in cache budget so a test host can run them.
    /// </summary>
    /// <remarks>
    /// The opposite of <see cref="SmallOptions"/>. Block, segment and fence sizes are the real
    /// defaults, because those are the values whose cost only shows up at volume; the memtable is the
    /// one knob deliberately lowered, to 64K entries, so a few hundred thousand records still force
    /// several flushes and a real tier merge instead of sitting in memory the whole run.
    /// </remarks>
    public static StoreOptions ThroughputOptions() => new()
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

    /// <summary>Builds a deterministic 55-character address from a seed.</summary>
    /// <remarks>
    /// Every character varies with the seed, the leading ones included, so distinct seeds land on
    /// distinct index keys and a reverse lookup confirms one candidate rather than thousands.
    /// </remarks>
    public static string Address(ulong seed)
    {
        Span<char> text = stackalloc char[AddressRecord.AddressLength];
        ulong state = unchecked((ulong)seed) + 0x1234_5678_9ABC_DEF0UL;
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = AddressAlphabet[(int)(Next(ref state) % (ulong)AddressAlphabet.Length)];
        }

        return new string(text);
    }

    /// <summary>Builds an address opening with <paramref name="prefix"/>, to force index key collisions.</summary>
    /// <remarks>
    /// The address index keys on the leading characters only, so two addresses built from the same
    /// prefix and different fillers reach the store as one key and have to be told apart by the
    /// confirmation read.
    /// </remarks>
    public static string AddressWithPrefix(string prefix, char filler) =>
        prefix + new string(filler, AddressRecord.AddressLength - prefix.Length);

    /// <summary>Appends <paramref name="value"/> to the bucket for <paramref name="key"/>, creating it if absent.</summary>
    /// <remarks>
    /// Builds the in-memory reference model that store lookups are compared against. Insertion order
    /// is preserved deliberately: a store returns matches in ascending ordinal order, so comparing the
    /// sequences checks ordering as well as membership.
    /// </remarks>
    public static void Track<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<TValue>? bucket))
        {
            map[key] = bucket = [];
        }

        bucket.Add(value);
    }

    public static ulong Next(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
