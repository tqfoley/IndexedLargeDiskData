using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Stores;

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
    private readonly BlockCache _cache;
    private bool _disposed;

    /// <summary>Opens or creates a data root at <paramref name="path"/>.</summary>
    /// <param name="path">Root directory. Created if missing.</param>
    /// <param name="options">Tuning, or null for the defaults.</param>
    public DataRoot(string path, StoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Options = options ?? new StoreOptions();
        Options.Validate();

        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(Path);

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

    /// <summary>Commits every store: records to disk, then indexes.</summary>
    public void Flush()
    {
        Transactions.Flush();
        Addresses.Flush();
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
    }
}
