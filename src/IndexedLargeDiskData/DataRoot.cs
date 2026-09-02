using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Records;
using IndexedLargeDiskData.Stores;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;

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

    public enum ByteOrder { BigEndian, LittleEndian }
    public static class Base58Binary
    {
        /// <summary>Decodes a Base58 string and renders it as a binary digit string.</summary>
        public static string ToBinary(string base58, ByteOrder order = ByteOrder.BigEndian, bool grouped = false)
        {
            byte[] bytes = Decode(base58);        // decoder from earlier
            if (order == ByteOrder.LittleEndian) Array.Reverse(bytes);
            return ToBinary(bytes, grouped);
        }

        /// <summary>Renders bytes as bits, MSB-first within each byte.</summary>
        public static string ToBinary(ReadOnlySpan<byte> bytes, bool grouped = false)
        {
            var sb = new StringBuilder(bytes.Length * (grouped ? 9 : 8));
            for (int i = 0; i < bytes.Length; i++)
            {
                if (grouped && i > 0) sb.Append(' ');
                for (int bit = 7; bit >= 0; bit--)
                    sb.Append((char)('0' + ((bytes[i] >> bit) & 1)));
            }
            return sb.ToString();
        }
    }

    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private static readonly int[] Map = BuildMap();
    public static byte[] Decode(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        BigInteger num = BigInteger.Zero;
        foreach (char c in s)
        {
            int d = c < 128 ? Map[c] : -1;
            if (d < 0) throw new FormatException($"Invalid Base58 character '{c}'.");
            num = num * 58 + d;
        }

        byte[] body = num.IsZero
            ? Array.Empty<byte>()
            : num.ToByteArray(isUnsigned: true, isBigEndian: true);

        int zeros = 0;
        while (zeros < s.Length && s[zeros] == '1') zeros++;

        var result = new byte[zeros + body.Length];
        body.CopyTo(result, zeros);
        return result;
    }

    private static int[] BuildMap()
    {
        var map = new int[128];
        Array.Fill(map, -1);
        for (int i = 0; i < Alphabet.Length; i++) map[Alphabet[i]] = i;
        return map;
    }

    public static ulong FromP2PKHAddressBase58DecodeFast(string s)
    {
        int zeros = 0;
        while (zeros < s.Length && s[zeros] == '1') zeros++;

        int size = (s.Length - zeros) * 733 / 1000 + 1;   // log(58)/log(256) ≈ 0.733
        Span<byte> buf = size <= 128 ? stackalloc byte[size] : new byte[size];
        buf.Clear();

        for (int i = zeros; i < s.Length; i++)
        {
            int carry = s[i] < 128 ? Map[s[i]] : -1;
            if (carry < 0) throw new FormatException($"Invalid Base58 character '{s[i]}'.");

            for (int j = size - 1; j >= 0; j--)
            {
                carry += 58 * buf[j];
                buf[j] = (byte)carry;
                carry >>= 8;
            }
        }

        int start = 0;
        while (start < size && buf[start] == 0) start++;

        var result = new byte[zeros + size - start];
        buf[start..].CopyTo(result.AsSpan(zeros));
        return FromHex(GetStringReverseHexBytes(result).Substring(5, 16));
    }

    public static string ByteArrayToHexString(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return string.Empty;

        return BitConverter.ToString(bytes).Replace("-", "");
    }

    public static string GetStringReverseHexBytes(byte[] hexBytes)
    {
        string hex = ByteArrayToHexString(hexBytes);
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
            throw new ArgumentException("Hex string must have an even number of characters.");

        int byteCount = hex.Length / 2;
        string[] bytePairs = new string[byteCount];

        // Break into byte-sized chunks
        for (int i = 0; i < byteCount; i++)
        {
            bytePairs[i] = hex.Substring(i * 2, 2);
        }

        // Reverse the byte order
        Array.Reverse(bytePairs);

        // Join into final hex string
        return string.Join("", bytePairs).ToLower();
    }

    public static ulong FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        return ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }




    public void AddSingleTransaction(ulong fromShort, string from, ulong toShort, string to, 
        ulong prevTransactionIdShort, string prevTransactionId, ulong transactionIdShort, string transactionId,
        ulong amount, ulong block)
    {
        List<ulong> a = GetAddressFromString(from.PadRight(AddressRecord.AddressLength, 'A'));
        if (a.Count == 0)
        {
            AddSingleAddress(fromShort, from.PadRight(AddressRecord.AddressLength, 'A'));
        }
        List<ulong> b = GetAddressFromString(to.PadRight(AddressRecord.AddressLength, 'A'));
        if (b.Count == 0)
        {
            AddSingleAddress(toShort, to.PadRight(AddressRecord.AddressLength, 'A'));
        }

        List<ulong> c = GetAddressFromString(prevTransactionId.PadRight(AddressRecord.AddressLength, 'T'));
        if (c.Count == 0)
        {
            AddSingleAddress(prevTransactionIdShort, prevTransactionId.PadRight(AddressRecord.AddressLength, 'T'));
        }
        List<ulong> d = GetAddressFromString(transactionId.PadRight(AddressRecord.AddressLength, 'T'));
        if (d.Count == 0)
        {
            AddSingleAddress(transactionIdShort, transactionId.PadRight(AddressRecord.AddressLength, 'T'));
        }

        var a1 = GetAddressFromString(from.PadRight(AddressRecord.AddressLength, 'A')).First(); // A for address, Frist because  some addersses get muyltiple short addresses (hash collision)
        var b1 = GetAddressFromString(to.PadRight(AddressRecord.AddressLength, 'A')).First(); //A for address, Frist because  some addersses get muyltiple short addresses (hash collision)

        var c1 = GetAddressFromString(prevTransactionId.PadRight(AddressRecord.AddressLength, 'T')).First(); //T for address, Frist because  some addersses get muyltiple short addresses (hash collision)
        var d1 = GetAddressFromString(transactionId.PadRight(AddressRecord.AddressLength, 'T')).First(); //T for address, Frist because  some addersses get muyltiple short addresses (hash collision)

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

    public List<QuadrupleRecord> GetTransactionV0(ulong v0)
    {
        return Transactions.FindByV0(v0).ToList();
    }

    public List<QuadrupleRecord> GetTransactionV1(ulong v1)
    {
        return Transactions.FindByV1(v1).ToList();
    }

    public List<QuadrupleRecord> GetTransactionV2(ulong v2)
    {
        return Transactions.FindByV2(v2).ToList();
    }

    public List<QuadrupleRecord> GetTransactionV3(ulong v3)
    {
        return Transactions.FindByV3(v3).ToList();
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
