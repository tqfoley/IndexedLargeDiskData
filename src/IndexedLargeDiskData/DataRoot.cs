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
    public const int BlockShift = 43;

    private readonly BlockCache _cache; 
    private readonly BlockLog _blockLog;
    private bool _disposed;


    public void AddSingleTransaction(ulong fromShort, string from, ulong toShort, string to, 
        ulong prevTransactionIdShort, string prevTransactionId, ulong transactionIdShort, string transactionId,
        ulong amount, int block)
    {
        List<ulong> a = GetAddressFromString(from.PadRight(55, 'A'));
        if (a.Count == 0)
        {
            AddSingleAddress(fromShort, from.PadRight(55, 'A'));
        }
        List<ulong> b = GetAddressFromString(to.PadRight(55, 'A'));
        if (b.Count == 0)
        {
            AddSingleAddress(toShort, to.PadRight(55, 'A'));
        }

        List<ulong> c = GetAddressFromString(prevTransactionId.PadRight(55, 'T'));
        if (c.Count == 0)
        {
            AddSingleAddress(prevTransactionIdShort, prevTransactionId.PadRight(55, 'T'));
        }
        List<ulong> d = GetAddressFromString(transactionId.PadRight(55, 'T'));
        if (d.Count == 0)
        {
            AddSingleAddress(transactionIdShort, transactionId.PadRight(55, 'T'));
        }

        var a1 = GetAddressFromString(from.PadRight(55, 'A')).First(); // A for address, Frist because  some addersses get muyltiple short addresses (hash collision)
        var b1 = GetAddressFromString(to.PadRight(55, 'A')).First(); //A for address, Frist because  some addersses get muyltiple short addresses (hash collision)

        var c1 = GetAddressFromString(prevTransactionId.PadRight(55, 'T')).First(); //T for address, Frist because  some addersses get muyltiple short addresses (hash collision)
        var d1 = GetAddressFromString(transactionId.PadRight(55, 'T')).First(); //T for address, Frist because  some addersses get muyltiple short addresses (hash collision)

        //if (r2.Count == 1)
        //{
        AddSingleTransaction(a1, b1, prevTransactionIdShort, transactionIdShort, (ulong)block, amount);
        //}
        //throw new Exception("bad");
        return;
    }

    /// <summary>Appends one transaction: sender, recipient, block number, amount and payload.</summary>
    /// <param name="v0">The sender. Indexed.</param>
    /// <param name="v1">The recipient. Indexed.</param>
    /// <param name="block">The block the transaction was seen in. Indexed, and logged.</param>
    /// <param name="amount">The amount. Indexed.</param>
    /// <param name="payload">
    /// A spare value carried on the record. Unindexed, so it comes back on a lookup made through one
    /// of the other four but can never be searched for; defaults to zero.
    /// </param>
    /// <remarks>
    /// The block number and the amount each get a field of their own, so neither is packed into the
    /// other and both are searchable in their own right.
    /// </remarks>
    public void AddSingleTransaction(ulong v0, ulong v1, ulong v2, ulong v3, ulong block, ulong amount)
    {
        if (block > 2 * 1024 * 1024 - 1) // 2^21
        {
            throw new ArgumentOutOfRangeException("block index too large");
        }

        if (amount > 1UL << BlockShift - 1)
        {
            //throw new ArgumentOutOfRangeException("amount too big");
            amount = 1UL << BlockShift - 1; 
        }

        ulong packedBlock = block << BlockShift;
        ulong v4 = packedBlock + amount;

        QuadrupleRecord[] batch = new QuadrupleRecord[1];
        batch[0] = new QuadrupleRecord(v0, v1, v2, v3, v4);


        Transactions.AppendRange(batch.AsSpan(0, 1));

        // After the append, so the log never names a block whose record did not land.
        _blockLog.Log(block);

        return;
    }

    public void AddSingleAddress(ulong v0, string address) 
    { 
        if(address.Length < 55)
        {
            address.PadRight(55, 'F');
        }

        AddressRecord[] batch3 = new AddressRecord[1];
        batch3[0] = new AddressRecord(v0, address);
        
        Addresses.AppendRange(batch3.AsSpan(0, 1));
        return;
    }

    public List<QuadrupleRecord> GetTransactionFromV0(ulong v0)
    {
        return Transactions.FindByV0(v0).ToList();
    }

    public List<QuadrupleRecord> GetTransactionToV1(ulong v1)
    {
        return Transactions.FindByV1(v1).ToList();
    }

    /// <summary>Every transaction written in <paramref name="block"/>.</summary>
    public List<QuadrupleRecord> GetTransactionsInBlock(ulong block)
    {
        return Transactions.FindByV2(block).ToList();
    }

    /// <summary>Every transaction whose amount is exactly <paramref name="amount"/>.</summary>
    public List<QuadrupleRecord> GetTransactionsByAmount(ulong amount)
    {
        return Transactions.FindByV3(amount).ToList();
    }


    public List<string> GetAddressFromLong(ulong v0)
    {
        List<string> ret = Addresses.FindById(v0).Select(x => x.Address).ToList();
        return ret;
    }


    public List<ulong> GetAddressFromString(string address)
    {

        if (address.Length < 55)
        {
            address = address.PadRight(55, 'A');
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

    /// <summary>Gets the store of four-value transactions.</summary>
    public TransactionStore Transactions { get; } = null!;

    /// <summary>Gets the store of identifier and address pairs.</summary>
    public AddressStore Addresses { get; } = null!;

    /// <summary>Gets the last block number written to <c>blockslog.txt</c>, or null if it is empty.</summary>
    /// <remarks>
    /// Answered from memory; the log file is only ever read when the root is opened. The time beside
    /// the number in the file is written for people and is not read back.
    /// </remarks>
    public ulong? LastLoggedBlock => _blockLog.LastBlock;

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

        ulong total = PlannedMaintenanceEntries;
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
    /// Called on the calling thread as merges advance. The percentage spans both stores and all six
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

        ulong total = Transactions.PlannedMaintenanceEntries + Addresses.PlannedMaintenanceEntries;
        MergeProgressTracker tracker = new(total, progress);

        Transactions.MaintainCore(tracker);
        Addresses.MaintainCore(tracker);
    }

    /// <summary>Estimates the index entries a <see cref="Maintain()"/> pass would write.</summary>
    public ulong PlannedMaintenanceEntries =>
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
