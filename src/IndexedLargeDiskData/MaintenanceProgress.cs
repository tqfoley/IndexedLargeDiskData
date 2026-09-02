namespace IndexedLargeDiskData;

/// <summary>A progress report raised while index tier merges are running.</summary>
/// <param name="EntriesWritten">Index entries written so far across the whole pass.</param>
/// <param name="TotalEntries">Entries the pass was estimated to write when it started.</param>
/// <param name="Stage">Which index is being merged, as <c>store/index</c>.</param>
/// <remarks>
/// <see cref="TotalEntries"/> is planned up front by simulating the merge cascade, so it is an
/// estimate: appends landing during a long pass can add work that was not in the plan. Use
/// <see cref="Percentage"/>, which is clamped, rather than dividing the raw fields.
/// </remarks>
public readonly record struct MaintenanceProgress(ulong EntriesWritten, ulong TotalEntries, string Stage)
{
    /// <summary>Gets progress through the pass, from 0 to 100.</summary>
    public double Percentage
    {
        get
        {
            if (TotalEntries <= 0)
            {
                return 100d;
            }
            else
            {
                return Math.Min(100d, EntriesWritten * 100d / TotalEntries);
            }
        }
    }
}

/// <summary>
/// Accumulates entry counts across several indexes so one pass reports a single percentage.
/// </summary>
/// <remarks>
/// Held by the caller that owns the whole pass — a <see cref="DataRoot"/> merging both stores, or a
/// single store merging its own indexes — so the total spans everything that pass will touch rather
/// than restarting at zero for each index.
/// </remarks>
internal sealed class MergeProgressTracker(ulong totalEntries, Action<MaintenanceProgress> sink)
{
    private ulong _written;

    internal void Advance(string stage, ulong entries)
    {
        _written += entries;
        sink(new MaintenanceProgress(_written, totalEntries, stage));
    }
}
