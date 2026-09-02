using System.Globalization;
using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Tests;

public class BlockLogTests
{
    /// <summary>Mirrors the shift <see cref="DataRoot"/> packs the block number up by.</summary>
    private const int BlockShift = 43;

    /// <summary>Reads the log back line by line, in the order the lines were written.</summary>
    /// <remarks>
    /// Opened sharing ReadWrite because the root under test still holds the file open for appending.
    /// </remarks>
    private static List<string> ReadLines(string rootPath)
    {
        using FileStream file = new(
            Path.Combine(rootPath, BlockLog.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(file);

        List<string> lines = [];
        while (reader.ReadLine() is string line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>Reads back the block numbers alone, dropping the timestamp on each line.</summary>
    private static List<ulong> ReadLog(string rootPath) =>
        [.. ReadLines(rootPath).Select(line => ulong.Parse(line.Split(' ')[0], CultureInfo.InvariantCulture))];

    [Fact]
    public void ARunOfTransactionsInOneBlockWritesOneLine()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        for (ulong i = 0; i < 50; i++)
        {
            root.AddSingleTransaction(i, i + 1, 0, 0, 100, 7);
        }

        root.AddSingleTransaction(99, 98, 0, 0, 101, 7);
        root.Flush();

        Assert.Equal([100UL, 101UL], ReadLog(dir.Path));
    }

    [Fact]
    public void ABlockSeenAgainAfterAnotherOneIsLoggedAgain()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.AddSingleTransaction(1, 1, 0, 0, 10, 1);
        root.AddSingleTransaction(2, 2, 0, 0, 11, 1);
        root.AddSingleTransaction(3, 3, 0, 0, 10, 1);
        root.Flush();

        // Only a repeat of the *previous* number is dropped. Nothing here searches the file for an
        // earlier occurrence, which is the point: the check is one comparison.
        Assert.Equal([10UL, 11UL, 10UL], ReadLog(dir.Path));
    }

    [Fact]
    public void TheBlockAndTheAmountArePackedIntoTheUnindexedFifthValue()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.AddSingleTransaction(1, 1, 10, 20, 55, 3);
        root.AddSingleTransaction(2, 2, 11, 21, 55, 4);
        root.AddSingleTransaction(3, 3, 12, 22, 56, 5);
        root.Flush();

        Assert.Equal([55UL, 56UL], ReadLog(dir.Path));
        Assert.Equal(56UL, root.LastLoggedBlock);

        // V4 is payload and carries the two of them packed together, so neither is a key: they come
        // back by unpacking a record some other lookup returned. Unsigned is what keeps the top of
        // that packed value a data bit rather than a sign bit.
        QuadrupleRecord found = Assert.Single(root.GetTransactionToV1(2));
        Assert.Equal(55UL, found.V4 >> BlockShift);
        Assert.Equal(4UL, found.V4 & ((1UL << BlockShift) - 1));
    }

    [Fact]
    public void ReopeningTheRootPicksUpWhereTheLogLeftOff()
    {
        using TempDirectory dir = new();

        using (DataRoot first = new(dir.Path, TestData.SmallOptions()))
        {
            first.AddSingleTransaction(1, 1, 0, 0, 200, 1);
            first.AddSingleTransaction(2, 2, 0, 0, 201, 1);
        }

        using DataRoot second = new(dir.Path, TestData.SmallOptions());
        Assert.Equal(201UL, second.LastLoggedBlock);

        second.AddSingleTransaction(3, 3, 0, 0, 201, 1);
        second.AddSingleTransaction(4, 4, 0, 0, 202, 1);
        second.Flush();

        Assert.Equal([200UL, 201UL, 202UL], ReadLog(dir.Path));
    }

    [Fact]
    public void EveryLineCarriesTheTimeTheBlockWasFirstSeen()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        root.AddSingleTransaction(1, 1, 0, 0, 300, 1);
        root.AddSingleTransaction(2, 2, 0, 0, 301, 1);
        root.Flush();
        DateTime after = DateTime.UtcNow.AddSeconds(1);

        List<string> lines = ReadLines(dir.Path);
        Assert.Equal(2, lines.Count);

        foreach (string line in lines)
        {
            string[] fields = line.Split(' ');
            Assert.Equal(2, fields.Length);

            DateTime stamped = DateTime.ParseExact(
                fields[1], "yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None);

            Assert.InRange(stamped, before, after);
        }
    }

    [Fact]
    public void TheStampMarksTheFirstTransactionInTheBlockNotTheLast()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.AddSingleTransaction(1, 1, 0, 0, 400, 1);
        DateTime afterTheFirst = DateTime.UtcNow.AddSeconds(1);

        Thread.Sleep(30);
        for (ulong i = 0; i < 20; i++)
        {
            root.AddSingleTransaction(i, i, 0, 0, 400, 1);
        }

        root.Flush();

        string[] fields = Assert.Single(ReadLines(dir.Path)).Split(' ');
        DateTime stamped = DateTime.ParseExact(
            fields[1], "yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None);

        // The later transactions in the block never reached the clock, let alone the file.
        Assert.True(stamped <= afterTheFirst, $"{stamped:O} should predate the rest of the block");
    }

    [Fact]
    public void AReopenStillRecoversTheLastBlockFromAStampedLine()
    {
        using TempDirectory dir = new();

        using (DataRoot first = new(dir.Path, TestData.SmallOptions()))
        {
            first.AddSingleTransaction(1, 1, 0, 0, 500, 1);
        }

        // The recovery read has to skip past the timestamp to find the number.
        using DataRoot second = new(dir.Path, TestData.SmallOptions());
        Assert.Equal(500UL, second.LastLoggedBlock);
    }

    [Fact]
    public void AnUnusedRootLeavesAnEmptyLog()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        Assert.Null(root.LastLoggedBlock);
        Assert.True(File.Exists(Path.Combine(dir.Path, BlockLog.FileName)));
        Assert.Empty(ReadLog(dir.Path));
    }
}
