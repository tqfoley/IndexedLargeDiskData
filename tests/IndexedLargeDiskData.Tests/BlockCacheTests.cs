using IndexedLargeDiskData.Caching;
using Microsoft.Win32.SafeHandles;

namespace IndexedLargeDiskData.Tests;

public class BlockCacheTests
{
    private const int BlockSize = 512;

    [Fact]
    public void Acquire_ReturnsTheBytesOnDisk()
    {
        using TempDirectory dir = new();
        byte[] content = Pattern(BlockSize * 4);
        string path = WriteFile(dir, content);

        using BlockCache cache = new(BlockSize, BlockSize * 16);
        using CachedFile file = Register(cache, path);

        for (int block = 0; block < 4; block++)
        {
            using BlockLease lease = file.Acquire(block);
            Assert.Equal(BlockSize, lease.Length);
            Assert.True(lease.Span.SequenceEqual(content.AsSpan(block * BlockSize, BlockSize)));
        }
    }

    [Fact]
    public void Acquire_ReturnsAShortLeaseForTheFinalPartialBlock()
    {
        using TempDirectory dir = new();
        byte[] content = Pattern(BlockSize + 17);
        string path = WriteFile(dir, content);

        using BlockCache cache = new(BlockSize, BlockSize * 16);
        using CachedFile file = Register(cache, path);

        using BlockLease lease = file.Acquire(1);
        Assert.Equal(17, lease.Length);
    }

    [Fact]
    public void Acquire_CountsHitsAndMisses()
    {
        using TempDirectory dir = new();
        string path = WriteFile(dir, Pattern(BlockSize * 2));

        using BlockCache cache = new(BlockSize, BlockSize * 16);
        using CachedFile file = Register(cache, path);

        file.Acquire(0).Dispose();
        file.Acquire(0).Dispose();
        file.Acquire(1).Dispose();

        Assert.Equal(2, cache.Misses);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void Acquire_EvictsOnceTheBudgetIsFull()
    {
        using TempDirectory dir = new();
        const int blocks = 64;
        string path = WriteFile(dir, Pattern(BlockSize * blocks));

        using BlockCache cache = new(BlockSize, BlockSize * 16);
        using CachedFile file = Register(cache, path);

        for (int block = 0; block < blocks; block++)
        {
            file.Acquire(block).Dispose();
        }

        Assert.Equal(16, cache.Capacity);
        Assert.True(cache.Resident <= cache.Capacity, $"resident {cache.Resident} exceeded capacity");
        Assert.True(cache.Evictions > 0, "expected the cache to have evicted something");
    }

    [Fact]
    public void EvictedBlocks_StillReadCorrectlyWhenReacquired()
    {
        using TempDirectory dir = new();
        const int blocks = 64;
        byte[] content = Pattern(BlockSize * blocks);
        string path = WriteFile(dir, content);

        using BlockCache cache = new(BlockSize, BlockSize * 16);
        using CachedFile file = Register(cache, path);

        for (int pass = 0; pass < 3; pass++)
        {
            for (int block = 0; block < blocks; block++)
            {
                using BlockLease lease = file.Acquire(block);
                Assert.True(lease.Span.SequenceEqual(content.AsSpan(block * BlockSize, BlockSize)));
            }
        }
    }

    [Fact]
    public void Invalidate_ForcesTheBlockToBeReread()
    {
        using TempDirectory dir = new();
        string path = WriteFile(dir, Pattern(BlockSize));

        using BlockCache cache = new(BlockSize, BlockSize * 16);
        using CachedFile file = Register(cache, path);

        file.Acquire(0).Dispose();

        byte[] replacement = new byte[BlockSize];
        replacement.AsSpan().Fill(0xAB);
        using (SafeFileHandle writer = File.OpenHandle(path, FileMode.Open, FileAccess.Write))
        {
            RandomAccess.Write(writer, replacement, 0);
        }

        file.Invalidate(0);

        using BlockLease lease = file.Acquire(0);
        Assert.True(lease.Span.SequenceEqual(replacement));
    }

    [Fact]
    public void Constructor_RejectsABlockSizeThatIsNotAPowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new BlockCache(1000, 1024 * 1024));
    }

    [Fact]
    public void Constructor_RejectsABudgetSmallerThanSixteenBlocks()
    {
        Assert.Throws<ArgumentException>(() => new BlockCache(BlockSize, BlockSize * 4));
    }

    private static byte[] Pattern(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 31) ^ (i >> 8));
        }

        return data;
    }

    private static string WriteFile(TempDirectory dir, byte[] content)
    {
        string path = Path.Combine(dir.Path, "blocks.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static CachedFile Register(BlockCache cache, string path) =>
        cache.Register(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), path);
}
