using System.Globalization;

namespace IndexedLargeDiskData.Tests;

public class BlockLogTests
{
    private const int BlockShift = 44;

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
    private static List<long> ReadLog(string rootPath) =>
        [.. ReadLines(rootPath).Select(line => long.Parse(line.Split(' ')[0], CultureInfo.InvariantCulture))];

    [Fact]
    public void ARunOfTransactionsInOneBlockWritesOneLine()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        for (int i = 0; i < 50; i++)
        {
            root.AddSingleTransaction(i, i + 1, 100, 7);
        }

        root.AddSingleTransaction(99, 98, 101, 7);
        root.Flush();

        Assert.Equal([100L, 101L], ReadLog(dir.Path));
    }

    [Fact]
    public void ABlockSeenAgainAfterAnotherOneIsLoggedAgain()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.AddSingleTransaction(1, 1, 10, 1);
        root.AddSingleTransaction(2, 2, 11, 1);
        root.AddSingleTransaction(3, 3, 10, 1);
        root.Flush();

        // Only a repeat of the *previous* number is dropped. Nothing here searches the file for an
        // earlier occurrence, which is the point: the check is one comparison.
        Assert.Equal([10L, 11L, 10L], ReadLog(dir.Path));
    }

    [Fact]
    public void BothOverloadsLogTheSameBlockNumber()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        root.AddSingleTransaction(1, 1, 55, 3);
        root.AddSingleTransaction(2, 2, (55L << BlockShift) + 4);
        root.AddSingleTransaction(3, 3, (56L << BlockShift) + 5);
        root.Flush();

        Assert.Equal([55L, 56L], ReadLog(dir.Path));
        Assert.Equal(56L, root.LastLoggedBlock);
    }

    [Fact]
    public void ReopeningTheRootPicksUpWhereTheLogLeftOff()
    {
        using TempDirectory dir = new();

        using (DataRoot first = new(dir.Path, TestData.SmallOptions()))
        {
            first.AddSingleTransaction(1, 1, 200, 1);
            first.AddSingleTransaction(2, 2, 201, 1);
        }

        using DataRoot second = new(dir.Path, TestData.SmallOptions());
        Assert.Equal(201L, second.LastLoggedBlock);

        second.AddSingleTransaction(3, 3, 201, 1);
        second.AddSingleTransaction(4, 4, 202, 1);
        second.Flush();

        Assert.Equal([200L, 201L, 202L], ReadLog(dir.Path));
    }

    [Fact]
    public void EveryLineCarriesTheTimeTheBlockWasFirstSeen()
    {
        using TempDirectory dir = new();
        using DataRoot root = new(dir.Path, TestData.SmallOptions());

        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        root.AddSingleTransaction(1, 1, 300, 1);
        root.AddSingleTransaction(2, 2, 301, 1);
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

        root.AddSingleTransaction(1, 1, 400, 1);
        DateTime afterTheFirst = DateTime.UtcNow.AddSeconds(1);

        Thread.Sleep(30);
        for (int i = 0; i < 20; i++)
        {
            root.AddSingleTransaction(i, i, 400, 1);
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
            first.AddSingleTransaction(1, 1, 500, 1);
        }

        // The recovery read has to skip past the timestamp to find the number.
        using DataRoot second = new(dir.Path, TestData.SmallOptions());
        Assert.Equal(500L, second.LastLoggedBlock);
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
