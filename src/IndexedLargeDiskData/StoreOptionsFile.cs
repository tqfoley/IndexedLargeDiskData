using System.Text.Json;

namespace IndexedLargeDiskData;

/// <summary>
/// The record of the <see cref="StoreOptions"/> a data root was created with, kept as JSON beside
/// the store directories.
/// </summary>
/// <remarks>
/// Several options decide the shape of what lands on disk — <see cref="StoreOptions.SegmentSize"/>
/// caps a data file, <see cref="StoreOptions.BlockSize"/> sizes every read, and the index knobs size
/// a segment's fences and Bloom filter. Reopening a directory with different values does not fail
/// where the mistake was made; it fails later and obscurely, as a segment that looks truncated or an
/// index that reads as corrupt. Writing the options once and checking them on every open turns that
/// into one exception naming the fields that moved.
/// </remarks>
internal static class StoreOptionsFile
{
    /// <summary>Name of the file, written in the data root beside the store directories.</summary>
    internal const string FileName = "options.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>
    /// Writes <paramref name="options"/> for a new data root, or checks them against the file an
    /// existing one already carries.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The directory holds an options file that does not match <paramref name="options"/>, or one
    /// that cannot be read.
    /// </exception>
    internal static void WriteOrVerify(string rootPath, StoreOptions options)
    {
        string path = Path.Combine(rootPath, FileName);
        if (File.Exists(path))
        {
            Verify(path, options);
            return;
        }

        // Written through a temporary name so an interrupted first open leaves no half-written file
        // for the next one to choke on.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(options, Format));
        File.Move(temporary, path, overwrite: true);
    }

    private static void Verify(string path, StoreOptions requested)
    {
        StoreOptions? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoreOptions>(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"'{path}' is not readable as store options.", e);
        }

        if (stored is null)
        {
            throw new InvalidDataException($"'{path}' is empty.");
        }

        List<string> differences = [.. Differences(stored, requested)];
        if (differences.Count == 0)
        {
            return;
        }

        throw new InvalidDataException(
            $"The options given do not match '{path}', which the data in this directory was written " +
            $"with: {string.Join(", ", differences)}. Pass the stored options, or open a new directory.");
    }

    /// <summary>Names every option whose stored value differs from the one being asked for.</summary>
    private static IEnumerable<string> Differences(StoreOptions stored, StoreOptions requested)
    {
        if (stored.BlockSize != requested.BlockSize)
            yield return Describe(nameof(StoreOptions.BlockSize), stored.BlockSize, requested.BlockSize);
        if (stored.CacheBudgetBytes != requested.CacheBudgetBytes)
            yield return Describe(nameof(StoreOptions.CacheBudgetBytes), stored.CacheBudgetBytes, requested.CacheBudgetBytes);
        if (stored.SegmentSize != requested.SegmentSize)
            yield return Describe(nameof(StoreOptions.SegmentSize), stored.SegmentSize, requested.SegmentSize);
        if (stored.MemTableEntries != requested.MemTableEntries)
            yield return Describe(nameof(StoreOptions.MemTableEntries), stored.MemTableEntries, requested.MemTableEntries);
        if (stored.MergeFanout != requested.MergeFanout)
            yield return Describe(nameof(StoreOptions.MergeFanout), stored.MergeFanout, requested.MergeFanout);
        if (stored.MaxSegmentEntries != requested.MaxSegmentEntries)
            yield return Describe(nameof(StoreOptions.MaxSegmentEntries), stored.MaxSegmentEntries, requested.MaxSegmentEntries);
        if (stored.FenceStride != requested.FenceStride)
            yield return Describe(nameof(StoreOptions.FenceStride), stored.FenceStride, requested.FenceStride);
        if (stored.BloomBitsPerKey != requested.BloomBitsPerKey)
            yield return Describe(nameof(StoreOptions.BloomBitsPerKey), stored.BloomBitsPerKey, requested.BloomBitsPerKey);
        if (stored.WriteBufferBytes != requested.WriteBufferBytes)
            yield return Describe(nameof(StoreOptions.WriteBufferBytes), stored.WriteBufferBytes, requested.WriteBufferBytes);
        if (stored.ReportMaintenanceProgress != requested.ReportMaintenanceProgress)
            yield return Describe(nameof(StoreOptions.ReportMaintenanceProgress), stored.ReportMaintenanceProgress, requested.ReportMaintenanceProgress);
    }

    private static string Describe<T>(string name, T stored, T requested) =>
        $"{name} is {stored} on disk but {requested} was given";
}
