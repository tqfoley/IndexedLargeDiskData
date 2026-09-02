using System.Globalization;

namespace IndexedLargeDiskData;

/// <summary>
/// An append-only record of the block numbers passed to <see cref="DataRoot"/>, written beside
/// <c>options.json</c> in the data root as one line per block: the number, a space, and the UTC time
/// the number was first seen.
/// </summary>
/// <remarks>
/// <para>
/// Ingest walks the chain in order, so the same block number arrives once per transaction in that
/// block — millions of times over a load. Only a change is worth a line, and deciding that has to
/// cost nothing: the last number written is held in a field and compared there, so a repeat is one
/// comparison and no file access at all. The file itself is read exactly once, when it is opened, to
/// carry that value across a restart; after that nothing ever reads it again.
/// </para>
/// <para>
/// The time is stamped where the line is written, which is the first append that carried this block
/// number, so the file doubles as a record of when each block was ingested and how long the one
/// before it took. UTC rather than local time: an ingest that straddles a daylight-saving change
/// would otherwise appear to run backwards.
/// </para>
/// <para>
/// Plain text rather than JSON because it is appended to forever. A JSON array would have to be
/// closed on every flush and reopened on the next append, which is the one thing an append-only file
/// should not need.
/// </para>
/// <para>
/// Writes are buffered and reach the disk on <see cref="Flush"/> or <see cref="Dispose"/>, both of
/// which <see cref="DataRoot"/> drives. Like the stores, this expects a single writer.
/// </para>
/// </remarks>
internal sealed class BlockLog : IDisposable
{
    /// <summary>Name of the file, written in the data root beside the store directories.</summary>
    internal const string FileName = "blockslog.txt";

    /// <summary>Stands in for "nothing logged yet", high enough that no real block number reaches it.</summary>
    /// <remarks>
    /// It was <c>long.MinValue</c> while block numbers were signed. Unsigned has no value below the
    /// range, so the sentinel moves to the top of it: zero is a perfectly good block number, and
    /// using it here would drop the first log line of a chain that starts at block zero.
    /// </remarks>
    private const ulong NoBlock = ulong.MaxValue;

    /// <summary>ISO 8601 to the millisecond, with the literal Z quoted so it survives as a literal.</summary>
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fff'Z'";

    /// <summary>
    /// Bytes read back from the end of an existing file to recover the last number logged.
    /// </summary>
    /// <remarks>
    /// Comfortably more than the longest line this can write — 20 digits of <see cref="long"/>, a
    /// space, a 24-character timestamp and a line break, so 47 — which means the final line always
    /// lands in the window whole. The partial line the window opens on is discarded.
    /// </remarks>
    private const int TailBytes = 128;

    private readonly StreamWriter _writer;
    private ulong _lastBlock;
    private bool _disposed;

    /// <summary>Opens the log in <paramref name="rootPath"/>, creating it if it is not there yet.</summary>
    /// <param name="rootPath">The data root directory, which must already exist.</param>
    internal BlockLog(string rootPath)
    {
        Path = System.IO.Path.Combine(rootPath, FileName);
        _lastBlock = ReadLastBlock(Path);
        // ReadWrite rather than Read so the file can be tailed while an ingest is running: a reader
        // that wants to open it alongside this handle has to permit the write access this one holds.
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = false,
        };
    }

    /// <summary>Gets the full path of the log file.</summary>
    internal string Path { get; }

    /// <summary>Gets the last block number logged, or null when nothing has been logged yet.</summary>
    internal ulong? LastBlock
    {
        get
        {
            if (_lastBlock == NoBlock)
            {
                return null;
            }

            return _lastBlock;
        }
    }

    /// <summary>Logs <paramref name="block"/> unless it repeats the number logged before it.</summary>
    /// <param name="block">The block number the transaction being appended belongs to.</param>
    /// <remarks>
    /// The clock is only read on the branch that writes, so the repeat path stays a comparison. That
    /// also makes the stamp mean what it says: the moment this block number was first written, not
    /// the moment some later transaction in the same block arrived.
    /// </remarks>
    internal void Log(ulong block)
    {
        // The reason the last value is kept in memory: the common case is a repeat, and it costs a
        // field read and a comparison rather than a seek.
        if (block == _lastBlock)
        {
            return;
        }

        _lastBlock = block;
        _writer.Write(block.ToString(CultureInfo.InvariantCulture));
        _writer.Write(' ');
        _writer.WriteLine(DateTime.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>Pushes buffered lines out to the file.</summary>
    internal void Flush() => _writer.Flush();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
    }

    /// <summary>Recovers the last number in an existing log, so a restart does not repeat it.</summary>
    /// <returns>The number on the final line, or <see cref="NoBlock"/> if there is not one to read.</returns>
    /// <remarks>
    /// Reads the tail of the file rather than the whole of it: after a long ingest this file holds a
    /// line per block, and only the last one says anything about what to write next. A line that will
    /// not parse is treated as no line at all, which costs one duplicate entry and keeps a hand-edited
    /// file from failing an open.
    /// </remarks>
    private static ulong ReadLastBlock(string path)
    {
        if (!File.Exists(path))
        {
            return NoBlock;
        }

        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.Length == 0)
        {
            return NoBlock;
        }

        int count = (int)Math.Min(TailBytes, file.Length);
        file.Seek(-count, SeekOrigin.End);

        byte[] tail = new byte[count];
        file.ReadExactly(tail, 0, count);

        string[] lines = System.Text.Encoding.UTF8.GetString(tail)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Only the final line is trusted. The window can open part way through the line before it,
        // and half of a number still parses as a number.
        if (lines.Length == 0)
        {
            return NoBlock;
        }

        // The number is the leading field; the timestamp after it is written for people, never read.
        string line = lines[^1];
        ReadOnlySpan<char> number;
        int space = line.IndexOf(' ');
        if (space < 0)
        {
            number = line;
        }
        else
        {
            number = line.AsSpan(0, space);
        }

        if (ulong.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong block))
        {
            return block;
        }

        return NoBlock;
    }
}
