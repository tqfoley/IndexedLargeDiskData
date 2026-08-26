
namespace IndexedLargeDiskData.Indexing;

/// <summary>
/// A blocked Bloom filter: every key touches bits inside a single 64-byte block.
/// </summary>
/// <remarks>
/// <para>
/// A classic Bloom filter spreads its k bits over the whole bit array, so probing a filter that does
/// not fit in memory costs k random reads. Confining all k bits to one cache-line-sized block turns
/// that into a single read, which is the difference between the filter helping and the filter being
/// more expensive than the search it is meant to skip.
/// </para>
/// <para>
/// This matters because lookups probe every segment in the level tree. Without a filter, a point
/// lookup pays a binary search per segment; with one, it pays a single block read per segment that
/// does not hold the key.
/// </para>
/// </remarks>
internal static class BlockedBloom
{
    /// <summary>The size of one filter block in bytes, chosen to match a cache line.</summary>
    internal const int BlockBytes = 64;

    private const int BitsPerBlock = BlockBytes * 8;
    private const int HashCount = 7;

    /// <summary>Computes how many blocks a filter needs for the given key count and bit budget.</summary>
    internal static int BlockCount(long keyCount, int bitsPerKey)
    {
        if (keyCount <= 0 || bitsPerKey <= 0)
        {
            return 0;
        }

        long bits = keyCount * bitsPerKey;
        long blocks = (bits + BitsPerBlock - 1) / BitsPerBlock;
        return (int)Math.Clamp(blocks, 1, int.MaxValue);
    }

    /// <summary>Sets the bits for <paramref name="key"/> in a filter held as a contiguous byte array.</summary>
    internal static void Add(Span<byte> filter, int blockCount, long key)
    {
        if (blockCount == 0)
        {
            return;
        }

        (int block, uint a, uint b) = Locate(key, blockCount);
        Span<byte> target = filter.Slice(block * BlockBytes, BlockBytes);

        for (int i = 0; i < HashCount; i++)
        {
            int bit = (int)((a + ((uint)i * b)) & (BitsPerBlock - 1));
            target[bit >> 3] |= (byte)(1 << (bit & 7));
        }
    }

    /// <summary>Tests one filter block for <paramref name="key"/>.</summary>
    /// <returns>False when the key is definitely absent; true when it may be present.</returns>
    internal static bool MayContain(ReadOnlySpan<byte> block, long key, int blockCount)
    {
        (_, uint a, uint b) = Locate(key, blockCount);

        for (int i = 0; i < HashCount; i++)
        {
            int bit = (int)((a + ((uint)i * b)) & (BitsPerBlock - 1));
            if ((block[bit >> 3] & (byte)(1 << (bit & 7))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets the index of the block that holds <paramref name="key"/>.</summary>
    internal static int BlockOf(long key, int blockCount) => Locate(key, blockCount).Block;

    private static (int Block, uint A, uint B) Locate(long key, int blockCount)
    {
        ulong h = Mix(unchecked((ulong)key));

        // Lemire's alternative to modulo: multiply-and-shift maps a 32-bit hash onto [0, blockCount).
        int block = (int)(((h >> 32) * (ulong)blockCount) >> 32);
        uint a = (uint)h;
        uint b = (uint)(h >> 32) | 1u;
        return (block, a, b);
    }

    private static ulong Mix(ulong value)
    {
        // splitmix64 finalizer: cheap, and good enough avalanche for filter bits.
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>Reads a filter block's byte offset within the filter section.</summary>
    internal static long OffsetOf(int block) => (long)block * BlockBytes;
}
