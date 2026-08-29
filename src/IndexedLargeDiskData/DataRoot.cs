using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Records;
using IndexedLargeDiskData.Stores;
using System.Net;

namespace IndexedLargeDiskData;

/// <summary>
/// A directory holding every store, and the one block cache they all read through.
/// </summary>
/// <remarks>
/// <para>
/// The cache lives here rather than inside each store because the budget is a property of the
/// machine, not of any one dataset. A single shared cache lets whichever store is being queried take
/// the memory it needs, instead of splitting 20 GiB into fixed slices that are wrong most of the time.
/// </para>
/// <para>
/// The cache commits its whole budget on construction, so create one <see cref="DataRoot"/> per
/// process and keep it for the process lifetime.
/// </para>
/// </remarks>
public sealed class DataRoot : IDisposable
{
    /// <summary>Bits the block number is shifted up by when it is packed with an amount into V2.</summary>
    private const int BlockShift = 44;

    private readonly BlockCache _cache;
    private readonly BlockLog _blockLog;
    private bool _disposed;


    public void AddSingleTransaction(long fromShort, string from, long toShort, string to, long amount, int block)
    {
        List<long> a = GetAddressFromString(from.PadRight(75, 'F'));
        if (a.Count == 0)
        {
            AddSingleAddress(fromShort, from.PadRight(75, 'F'));
        }
        List<long> b = GetAddressFromString(to.PadRight(75, 'F'));
        if (b.Count == 0)
        {
            AddSingleAddress(toShort, to.PadRight(75, 'F'));
        }
        var a1 = GetAddressFromString(from.PadRight(75, 'F')).First(); // some addersses get two short addresses
        var b1 = GetAddressFromString(from.PadRight(75, 'F')).First(); // some addersses get two short addresses

        //if (r2.Count == 1)
        //{
        AddSingleTransaction(a1, b1, block, amount);
        //}
        //throw new Exception("bad");
        return;
    }

    public void AddSingleTransaction(long v0, long v1, long block, long amount)
    {
        if (block > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException("block index too large");
        }

        if (amount > 1 << 43)
        {
            //throw new ArgumentOutOfRangeException("amount too big");
            amount = 1 << 43;
        }

        long packedBlock = block << BlockShift;
        long v2 = packedBlock + amount;

        TripleRecord[] batch = new TripleRecord[1];
        batch[0] = new TripleRecord(v0, v1, v2);

        Transactions.AppendRange(batch.AsSpan(0, 1));

        // After the append, so the log never names a block whose record did not land.
        _blockLog.Log(block);

        return;
    }

    /*public void AddSingleTransaction(long v0, long v1, long v2)
    {

        TripleRecord[] batch = new TripleRecord[1];
        batch[0] = new TripleRecord(v0, v1, v2);

        Transactions.AppendRange(batch.AsSpan(0, 1));

        // V2 carries the block number in its high bits, packed by the overload above.
        _blockLog.Log(v2 >>> BlockShift);

        return;
    }*/

    public void AddSingleAddress(long v0, string address) 
    { 
        if(address.Length < 75)
        {
            address.PadRight(75, 'F');
        }

        AddressRecord[] batch3 = new AddressRecord[1];
        batch3[0] = new AddressRecord(v0, address);
        
        Addresses.AppendRange(batch3.AsSpan(0, 1));
        return;
    }

    public List<TripleRecord> GetTransactionFromV0(long v0)
    {
        return Transactions.FindByV0(v0).ToList();
    }

    public List<TripleRecord> GetTransactionFromV1(long v1)
    {
        return Transactions.FindByV1(v1).ToList();
    }
    //public List<TripleRecord> GetTransactionFromV2(long v2)
    //{
        //return Transactions.Fin(v2).ToList();
    //}


    public List<string> GetAddressFromLong(long v0)
    {
        List<string> ret = Addresses.FindById(v0).Select(x => x.Address).ToList();
        return ret;
    }


    public List<long> GetAddressFromString(string address)
    {

        if (address.Length < 75)
        {
            address.PadRight(75, 'F');
        }

        var ret = Addresses.FindByAddress(address).Select(x => x.Id).ToList();
        return ret;
    }

    /// <summary>Opens or creates a data root at <paramref name="path"/>.</summary>
    /// <param name="path">Root directory. Created if missing.</param>
    /// <param name="options">Tuning, or null for the defaults.</param>
    /// <remarks>
    /// A new root records its options as <c>options.json</c>; an existing one is checked against the
    /// file it already carries, so a directory can only ever be opened with the options its data was
    /// written with. The block log, <c>blockslog.txt</c>, is opened alongside it and appended to.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// <paramref name="path"/> holds an <c>options.json</c> that does not match
    /// <paramref name="options"/>.
    /// </exception>
    public DataRoot(string path, StoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Options = options ?? new StoreOptions();
        Options.Validate();

        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(Path);

        // Before the cache commits its budget, so a directory opened with the wrong options fails
        // on the mismatch rather than after allocating for it.
        StoreOptionsFile.WriteOrVerify(Path, Options);

        _blockLog = new BlockLog(Path);
        _cache = new BlockCache(Options);

        try
        {
            Transactions = new TransactionStore(System.IO.Path.Combine(Path, "transactions"), Options, _cache);
            Addresses = new AddressStore(System.IO.Path.Combine(Path, "addresses"), Options, _cache);
        }
        catch
        {
            Transactions?.Dispose();
            _cache.Dispose();
            _blockLog.Dispose();
            throw;
        }
    }

    /// <summary>Gets the absolute path of the root directory.</summary>
    public string Path { get; }

    /// <summary>Gets the options in force for every store here.</summary>
    public StoreOptions Options { get; }

    /// <summary>Gets the shared block cache, mainly for its hit and miss counters.</summary>
    public BlockCache Cache => _cache;

    /// <summary>Gets the store of three-value transactions.</summary>
    public TransactionStore Transactions { get; } = null!;

    /// <summary>Gets the store of identifier and address pairs.</summary>
    public AddressStore Addresses { get; } = null!;

    /// <summary>Gets the last block number written to <c>blockslog.txt</c>, or null if it is empty.</summary>
    /// <remarks>
    /// Answered from memory; the log file is only ever read when the root is opened. The time beside
    /// the number in the file is written for people and is not read back.
    /// </remarks>
    public long? LastLoggedBlock => _blockLog.LastBlock;

    /// <summary>Commits every store: records to disk, then indexes.</summary>
    public void Flush()
    {
        Transactions.Flush();
        Addresses.Flush();
        _blockLog.Flush();
    }

    /// <summary>Runs index tier merges across every store, reporting progress to the console.</summary>
    /// <remarks>
    /// Console output is controlled by <see cref="StoreOptions.ReportMaintenanceProgress"/>. Use the
    /// <see cref="Maintain(Action{MaintenanceProgress})"/> overload to route progress somewhere else.
    /// </remarks>
    public void Maintain()
    {
        if (!Options.ReportMaintenanceProgress)
        {
            Transactions.MaintainCore(null);
            Addresses.MaintainCore(null);
            return;
        }

        long total = PlannedMaintenanceEntries;
        if (total == 0)
        {
            ConsoleMaintenanceReporter.ReportNothingToDo();
            return;
        }

        ConsoleMaintenanceReporter reporter = new(total);
        MergeProgressTracker tracker = new(total, reporter.Report);

        Transactions.MaintainCore(tracker);
        Addresses.MaintainCore(tracker);

        reporter.Finish(total);
    }

    /// <summary>Runs index tier merges across every store, reporting progress as it goes.</summary>
    /// <param name="progress">
    /// Called on the calling thread as merges advance. The percentage spans both stores and all four
    /// indexes, so it runs once from 0 to 100 rather than restarting for each index.
    /// </param>
    /// <example>
    /// <code>
    /// root.Maintain(p =&gt; Console.Write($"\rmerging {p.Percentage:F1}%  {p.Stage}   "));
    /// </code>
    /// </example>
    public void Maintain(Action<MaintenanceProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        long total = Transactions.PlannedMaintenanceEntries + Addresses.PlannedMaintenanceEntries;
        MergeProgressTracker tracker = new(total, progress);

        Transactions.MaintainCore(tracker);
        Addresses.MaintainCore(tracker);
    }

    /// <summary>Estimates the index entries a <see cref="Maintain()"/> pass would write.</summary>
    public long PlannedMaintenanceEntries =>
        Transactions.PlannedMaintenanceEntries + Addresses.PlannedMaintenanceEntries;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Transactions.Dispose();
        Addresses.Dispose();
        _cache.Dispose();
        _blockLog.Dispose();
    }
}
