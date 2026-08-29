using IndexedLargeDiskData;
using IndexedLargeDiskData.Records;
using IndexedLargeDiskData.Stores;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Transactions;

namespace IndexedLargeDiskData.Cli;

/// <summary>Entry point for the command line front end.</summary>
/// <remarks>
/// A thin shell over the library: it parses arguments, opens a <see cref="DataRoot"/> and prints
/// results. All behaviour lives in the library so it can be exercised without a process boundary.
/// </remarks>
internal static class Program
{
    private const long DefaultCacheMegabytes = 256;

    /// <summary>Characters that synthetic addresses are drawn from.</summary>
    private const string AddressAlphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Runs the CLI.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <returns>Zero on success, non-zero on failure.</returns>
    private static int Main(string[] args)
    { 
        Roundtrip();
        return 0;
    }

    /// <summary>
    /// Writes, closes, reopens, writes more, commits, closes, then reopens and queries.
    /// </summary>
    /// <remarks>
    /// Every lookup is checked against a model built while writing, so this holds roughly 50 bytes
    /// per record in memory: keep <c>--count</c> to a few million. The exit code is non-zero if any
    /// check fails.
    /// </remarks>
    private static int Roundtrip()
    {
        long perSession = 2_123_000;
        long addressesPerSession =  150_000;
        long distinctKeys =  2_000;
        long blockNumberCurrent = 0;

        // A fresh directory per run: the record counts below are exact, and reopening a directory
        // that already holds data would append to it and fail every one of them.
        string dataPath = "c:\\unittest\\" + Path.Combine("root", "temp");// $"roundtrip-{Guid.NewGuid().ToString("n")[..8]}");


        StoreOptions options = new StoreOptions()
        {
            BlockSize = 4096,
            CacheBudgetBytes = 1600L * 1024 * 1024, // 1.6 gigs
            SegmentSize = 500L * 1024 * 1024, // 500 megs
            MemTableEntries = 1 << 16,
            MaxSegmentEntries = 1 << 20,
            MergeFanout = 4,
            FenceStride = 4096,
            BloomBitsPerKey = 10,
            WriteBufferBytes = 1 << 20,
        };

        Console.WriteLine($"data       {dataPath}");
        Console.WriteLine($"options    memtable={options.MemTableEntries:N0} entries, " +
                          $"segment={options.SegmentSize / (1024 * 1024):N0} MiB, " +
                          $"cache={options.CacheBudgetBytes / (1024 * 1024):N0} MiB");

        Dictionary<long, List<TripleRecord>> byV0 = [];
        Dictionary<long, List<TripleRecord>> byV1 = [];
        ulong seed = 20250825;
        int failures = 0;
        Stopwatch clock = Stopwatch.StartNew();

        // Session one: write, commit to disk, close the data files.
        using (DataRoot db = new(dataPath, options))
        {
            AppendTransactions(db, byV0, byV1, ref seed, 0, perSession, distinctKeys);
            AppendAddresses(db, 0, addressesPerSession);

            long v0 = 1111111111111;
            long v3 = 3333333333333;
            for (long h = 0; h < perSession * 3L; h++)
            {
                TripleRecord[] batch = new TripleRecord[1];
                batch[0] = new TripleRecord(v0++, v0++, v3);

                db.Transactions.AppendRange(batch.AsSpan(0, 1));
            }



            db.Flush();

            //failures += Check("session 1 record count", perSession, db.Transactions.Count);
            //failures += Check("session 1 committed count", perSession, db.Transactions.CommittedCount);
        }

        Console.WriteLine($"session 1  wrote {perSession:N0} transactions, {addressesPerSession:N0} addresses" +
                          $"  [{clock.Elapsed.TotalSeconds:F2}s]");

        // Session two: reopen, confirm session one survived, append more, commit, close.
        clock.Restart();
        using (DataRoot db = new(dataPath, options))
        {
            //failures += Check("reopened record count", perSession, db.Transactions.Count);
            //failures += Check("reopened address count", addressesPerSession, db.Addresses.Count);

            // Readable straight after reopen, before anything new is written: the index was rebuilt
            // from the records rather than needing the previous process still to be alive.
            //if (db.Transactions.FindByV0(0).Count == 0)
            {
                //Console.Error.WriteLine("  FAIL lookup after reopen returned nothing");
                //failures++;
            }

                long v0 = 1111111111111;
                long v1 = 2222222222222;
                long v3 = 3333333333333;
            TripleRecord[] batch = new TripleRecord[1];
            batch[0] = new TripleRecord(v0, v1, v3);

            db.Transactions.AppendRange(batch.AsSpan(0, 1));

            for (int i = 0; i < 5555; i++)
            {
                long v20 = 1411111111111;
                long v21 = 2422222222222;
                blockNumberCurrent++;
                db.AddSingleTransaction(v20, v21, blockNumberCurrent / 64, 1 << 42);
            }

            AddressRecord[] batch3 = new AddressRecord[1];
            batch3[0] = new AddressRecord(v0, "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNaxxxxxxx");
            db.Addresses.AppendRange(batch3.AsSpan(0, 1));

            AddressRecord[] batch2 = new AddressRecord[1];
            batch2[0] = new AddressRecord(v3, "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNaxxxxxxx");
            db.Addresses.AppendRange(batch2.AsSpan(0, 1));


            //AppendTransactions(db, byV0, byV1, ref seed, perSession, perSession, distinctKeys);
            //AppendAddresses(db, addressesPerSession, addressesPerSession);

            db.Flush();
            db.Maintain();

            //failures += Check("session 2 committed count", perSession * 2, db.Transactions.CommittedCount);
        }

        Console.WriteLine($"session 2  wrote {perSession:N0} transactions, {addressesPerSession:N0} addresses" +
                          $"  [{clock.Elapsed.TotalSeconds:F2}s]");

        /*public void AddSingleAddress(long v0, string address)
TripleRecord> GetTransactionFromV0(long v0)
t<string> GetAddressFromLong(long v0)
GetAddressString(string address)
        */

        // Session three: reopen once more and query only.
        clock.Restart();
        using (DataRoot db = new(dataPath, options))
        {
            long v0 = 1111111111111;
            long v1 = 2222222222222;
            long v3 = 3333333333333;

            long v20 = 1411111111111;
            long v21 = 2422222222222;

            var t2 = db.GetTransactionFromV0(v20);
            var t3 = db.GetTransactionFromV1(v21);

            long byFirst = db.Transactions.CountByV0(v0);
            long bySecond = db.Transactions.CountByV1(v1);
            Console.WriteLine($"           v0={byFirst:N0}  v1={bySecond:N0}");

            var result1 = db.Transactions.FindByV0(v0);
            var result2 = db.Transactions.FindByV1(v0);
            var result3 = db.Transactions.FindByV1(v1);

            Console.WriteLine(result1.Count);
            Console.WriteLine(result2.Count);
            Console.WriteLine(result3.Count);

            var resultAddress1 = db.Addresses.FindByAddress("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNaxxxxxxx");

            Console.WriteLine(resultAddress1.Count);


            var r1 = db.GetAddressFromLong(v0);
            var r2 = db.GetAddressFromString("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNaxxxxxxx");
            Console.WriteLine("r1");
            foreach(var r in r1)
            {
                Console.WriteLine(r.ToString());
            }
            Console.WriteLine("r2");
            foreach (var r in r2)
            {
                Console.WriteLine(r.ToString());
            }

            Console.WriteLine($"total transactions {db.Transactions.Count:N0}");
            Console.WriteLine($"total addresses    {db.Addresses.Count:N0}");

            Console.WriteLine($"total transactions {db.Transactions.Count:N0}");
            Console.WriteLine($"total addresses    {db.Addresses.Count:N0}");


            //ReportTransactionCounts(db, [1111111111111, 2222222222222, 9999999999999]);

            /*failures += Check("final record count", perSession * 2, db.Transactions.Count);
            failures += Check("final address count", addressesPerSession * 2, db.Addresses.Count);

            long spanning = 0;
            for (long key = 0; key < distinctKeys; key++)
            {
                IReadOnlyList<TripleRecord> matches = db.Transactions.FindByV0(key);
                failures += CheckSequence($"v0={key}", byV0.GetValueOrDefault(key, []), matches);
                failures += CheckSequence($"v1={key}", byV1.GetValueOrDefault(key, []), db.Transactions.FindByV1(key));

                // V2 carries the ordinal, so it says which session wrote the record.
                if (matches.Any(r => r.V2 < perSession) && matches.Any(r => r.V2 >= perSession))
                {
                    spanning++;
                }
            }

            for (long id = 0; id < addressesPerSession * 2; id += 97)
            {
                if (!db.Addresses.TryGetAddress(id, out string? address) || address != SyntheticAddress(id))
                {
                    Console.Error.WriteLine($"  FAIL address for id {id}");
                    failures++;
                }
                else if (!db.Addresses.TryGetId(address, out long found) || found != id)
                {
                    Console.Error.WriteLine($"  FAIL reverse lookup for id {id}");
                    failures++;
                }
            }

            Console.WriteLine($"session 3  verified {distinctKeys * 2:N0} lookups, " +
                              $"{spanning:N0}/{distinctKeys:N0} keys span both sessions" +
                              $"  [{clock.Elapsed.TotalSeconds:F2}s]");
            PrintCache(db);
            PrintIndexes(db);*/
        }

        if (failures == 0)
        {
            Console.WriteLine("OK");
            return 0;
        }
        else
        {
            Console.WriteLine($"FAILED with {failures} problem(s)");
            return 1;
        }
    }

    /// <summary>Prints the store total and, for each probe value, how many transactions carry it.</summary>
    /// <remarks>
    /// Uses <c>CountByV0</c> / <c>CountByV1</c> rather than <c>FindByV0(..).Count</c>: the count is
    /// answered from the index, while fetching the records would mean one random read each. On a key
    /// with a thousand matches that is a thousand reads paid for a number.
    /// </remarks>
    private static void ReportTransactionCounts(DataRoot db, params long[] probes)
    {
        Console.WriteLine($"counts     {db.Transactions.Count:N0} transactions in the store");

        foreach (long probe in probes)
        {
            long byFirst = db.Transactions.CountByV0(probe);
            long bySecond = db.Transactions.CountByV1(probe);
            Console.WriteLine($"           {probe,15:N0}  v0={byFirst:N0}  v1={bySecond:N0}");
        }
    }

    private static void AppendTransactions(
        DataRoot db,
        Dictionary<long, List<TripleRecord>> byV0,
        Dictionary<long, List<TripleRecord>> byV1,
        ref ulong seed,
        long firstOrdinal,
        long count,
        long distinctKeys)
    {
        TripleRecord[] batch = new TripleRecord[8192];
        long written = 0;

        while (written < count)
        {
            int take = (int)Math.Min(batch.Length, count - written);
            for (int i = 0; i < take; i++)
            {
                long v0 = (long)(Next(ref seed) % (ulong)distinctKeys);
                long v1 = (long)(Next(ref seed) % (ulong)distinctKeys);
                batch[i] = new TripleRecord(v0, v1, firstOrdinal + written + i);

                Track(byV0, v0, batch[i]);
                Track(byV1, v1, batch[i]);
            }

            db.Transactions.AppendRange(batch.AsSpan(0, take));
            written += take;
        }
    }

    private static void AppendAddresses(DataRoot db, long firstId, long count)
    {
        AddressRecord[] batch = new AddressRecord[8192];
        long written = 0;

        while (written < count)
        {
            int take = (int)Math.Min(batch.Length, count - written);
            for (int i = 0; i < take; i++)
            {
                long id = firstId + written + i;
                batch[i] = new AddressRecord(id, SyntheticAddress(id));
            }

            db.Addresses.AppendRange(batch.AsSpan(0, take));
            written += take;
        }
    }

    private static void Track<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<TValue>? bucket))
        {
            map[key] = bucket = [];
        }

        bucket.Add(value);
    }

    private static int Check(string label, long expected, long actual)
    {
        if (expected == actual)
        {
            return 0;
        }

        Console.Error.WriteLine($"  FAIL {label}: expected {expected:N0}, got {actual:N0}");
        return 1;
    }

    private static int CheckSequence(string label, List<TripleRecord> expected, IReadOnlyList<TripleRecord> actual)
    {
        if (expected.Count == actual.Count && expected.SequenceEqual(actual))
        {
            return 0;
        }

        Console.Error.WriteLine($"  FAIL {label}: expected {expected.Count:N0} records, got {actual.Count:N0}");
        return 1;
    }

    private static int Dispatch(string command, Arguments args) => command switch
    {
        "ingest-transactions" => IngestTransactions(args),
        "ingest-addresses" => IngestAddresses(args),
        "find-v0" => FindTransactions(args, byV1: false),
        "find-v1" => FindTransactions(args, byV1: true),
        "find-id" => FindById(args),
        "find-address" => FindByAddress(args),
        "maintain" => Maintain(args),
        "stats" => Stats(args),
        //"roundtrip" => Roundtrip(args),
        _ => Unknown(command),
    };

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'");
        PrintUsage();
        return 1;
    }

    private static int IngestTransactions(Arguments args)
    {
        long count = args.RequireLong("count");
        long distinctKeys = args.OptionalLong("keys") ?? Math.Max(count / 8, 1);

        using DataRoot root = Open(args);
        Stopwatch clock = Stopwatch.StartNew();

        ulong seed = (ulong)root.Transactions.Count + 1;
        TripleRecord[] batch = new TripleRecord[4096];

        long written = 0;
        while (written < count)
        {
            int take = (int)Math.Min(batch.Length, count - written);
            for (int i = 0; i < take; i++)
            {
                long v0 = (long)(Next(ref seed) % (ulong)distinctKeys);
                long v1 = (long)(Next(ref seed) % (ulong)distinctKeys);
                long v2 = (long)(Next(ref seed) & long.MaxValue);
                batch[i] = new TripleRecord(v0, v1, v2);
            }

            root.Transactions.AppendRange(batch.AsSpan(0, take));
            written += take;
        }

        root.Flush();
        clock.Stop();

        Console.WriteLine($"appended {written:N0} transactions in {clock.Elapsed.TotalSeconds:F2}s " +
                          $"({written / Math.Max(clock.Elapsed.TotalSeconds, 0.001):N0}/s)");
        Console.WriteLine($"total records: {root.Transactions.Count:N0}");
        return 0;
    }

    private static int IngestAddresses(Arguments args)
    {
        long count = args.RequireLong("count");

        using DataRoot root = Open(args);
        Stopwatch clock = Stopwatch.StartNew();

        long start = root.Addresses.Count;
        AddressRecord[] batch = new AddressRecord[4096];

        long written = 0;
        while (written < count)
        {
            int take = (int)Math.Min(batch.Length, count - written);
            for (int i = 0; i < take; i++)
            {
                long id = start + written + i;
                batch[i] = new AddressRecord(id, SyntheticAddress(id));
            }

            root.Addresses.AppendRange(batch.AsSpan(0, take));
            written += take;
        }

        root.Flush();
        clock.Stop();

        Console.WriteLine($"appended {written:N0} addresses in {clock.Elapsed.TotalSeconds:F2}s " +
                          $"({written / Math.Max(clock.Elapsed.TotalSeconds, 0.001):N0}/s)");
        Console.WriteLine($"total records: {root.Addresses.Count:N0}");
        return 0;
    }

    private static int FindTransactions(Arguments args, bool byV1)
    {
        long key = args.RequireLong("key");
        using DataRoot root = Open(args);

        Stopwatch clock = Stopwatch.StartNew();
        IReadOnlyList<TripleRecord> matches;
        if (byV1)
        {
            matches = root.Transactions.FindByV1(key);
        }
        else
        {
            matches = root.Transactions.FindByV0(key);
        }
        clock.Stop();

        Console.WriteLine($"{matches.Count:N0} match(es) in {clock.Elapsed.TotalMilliseconds:F2}ms");
        foreach (TripleRecord record in matches.Take(20))
        {
            Console.WriteLine($"  {record.V0}  {record.V1}  {record.V2}");
        }

        if (matches.Count > 20)
        {
            Console.WriteLine($"  ... {matches.Count - 20:N0} more");
        }

        PrintCache(root);
        return 0;
    }

    private static int FindById(Arguments args)
    {
        long id = args.RequireLong("id");
        using DataRoot root = Open(args);

        if (root.Addresses.TryGetAddress(id, out string? address))
        {
            Console.WriteLine(address);
            PrintCache(root);
            return 0;
        }

        Console.Error.WriteLine($"no address for id {id}");
        return 2;
    }

    private static int FindByAddress(Arguments args)
    {
        string address = args.Require("address");
        using DataRoot root = Open(args);

        if (root.Addresses.TryGetId(address, out long id))
        {
            Console.WriteLine(id.ToString());
            PrintCache(root);
            return 0;
        }

        Console.Error.WriteLine($"no id for address {address}");
        return 2;
    }

    private static int Maintain(Arguments args)
    {
        // The library prints the meter itself now; the command just adds the resulting index shape.
        using DataRoot root = Open(args);
        root.Maintain();
        PrintIndexes(root);
        return 0;
    }

    private static int Stats(Arguments args)
    {
        using DataRoot root = Open(args);

        Console.WriteLine($"root:         {root.Path}");
        Console.WriteLine($"block size:   {root.Options.BlockSize:N0} bytes");
        Console.WriteLine($"cache blocks: {root.Cache.Capacity:N0} " +
                          $"({(double)root.Cache.Capacity * root.Options.BlockSize / (1024 * 1024):N0} MiB)");
        Console.WriteLine($"transactions: {root.Transactions.Count:N0} records " +
                          $"({root.Transactions.Count * TripleRecord.RecordSize / 1024.0 / 1024:N1} MiB)");
        Console.WriteLine($"addresses:    {root.Addresses.Count:N0} records " +
                          $"({root.Addresses.Count * AddressRecord.RecordSize / 1024.0 / 1024:N1} MiB)");
        PrintIndexes(root);
        return 0;
    }

    private static void PrintIndexes(DataRoot root)
    {
        Console.WriteLine("indexes:");
        Report("transactions", root.Transactions.GetIndexStatistics());
        Report("addresses", root.Addresses.GetIndexStatistics());

        static void Report(string store, IReadOnlyList<IndexStatistics> stats)
        {
            foreach (IndexStatistics stat in stats)
            {
                Console.WriteLine($"  {store}/{stat.Name,-8} segments={stat.SegmentCount,-4} " +
                                  $"buffered={stat.PendingCount,-8:N0} covers={stat.CoveredUpTo:N0}");
            }
        }
    }

    private static void PrintCache(DataRoot root) =>
        Console.WriteLine($"cache: {root.Cache.Hits:N0} hits, {root.Cache.Misses:N0} misses, " +
                          $"{root.Cache.Evictions:N0} evictions, {root.Cache.Resident:N0} blocks resident");

    private static DataRoot Open(Arguments args)
    {
        return new DataRoot(args.Require("root"), OptionsFor(args));
    }

    /// <summary>Builds the store options from the shared command line switches.</summary>
    /// <remarks>
    /// The library defaults to a 20 GiB cache; the CLI starts small so a demo run does not commit the
    /// whole budget up front. <c>--memtable</c> is exposed because it is the knob that decides how
    /// much work a later merge has to do: a small memtable means many level-0 segments and several
    /// cascade rounds, each rewriting every entry again.
    /// </remarks>
    private static StoreOptions OptionsFor(Arguments args) => new()
    {
        CacheBudgetBytes = (args.OptionalLong("cache-mb") ?? DefaultCacheMegabytes) * 1024 * 1024,
        MemTableEntries = (int)(args.OptionalLong("memtable") ?? (1 << 20)),
    };

    /// <summary>Derives a deterministic 75-character address from an identifier.</summary>
    /// <remarks>
    /// Every character varies with the identifier, the leading ones included. Addresses that all
    /// opened with the same fixed tag would share one index key and turn every reverse lookup into a
    /// scan of the whole synthetic set.
    /// </remarks>
    private static string SyntheticAddress(long id)
    {
        Span<char> text = stackalloc char[AddressRecord.AddressLength];
        ulong state = unchecked((ulong)id) + 0x1234_5678_9ABC_DEF0UL;
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = AddressAlphabet[(int)(Next(ref state) % (ulong)AddressAlphabet.Length)];
        }

        return new string(text);
    }

    private static ulong Next(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            usage: indexedlargediskdata <command> --root <dir> [options]

            commands:
              ingest-transactions  --count <n> [--keys <n>]   append synthetic three-value records
              ingest-addresses     --count <n>                append synthetic id/address pairs
              find-v0              --key <n>                  records whose first value matches
              find-v1              --key <n>                  records whose second value matches
              find-id              --id <n>                   address stored against an id
              find-address         --address <75 chars>       id stored against an address
              maintain                                        merge index tiers
              stats                                           record counts and index shape
              roundtrip            [--count <n>]              write, close, reopen, write, reopen, verify

            options:
              --cache-mb <n>   block cache budget in MiB (default 256)
              --memtable <n>   index entries buffered before a flush (default 1048576)
            """);
    }

    /// <summary>Minimal --name value argument parsing.</summary>
    private readonly struct Arguments
    {
        private readonly Dictionary<string, string> _values;

        internal Arguments(ReadOnlySpan<string> args)
        {
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"unexpected argument '{args[i]}'");
                }

                string name = args[i][2..];
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"--{name} needs a value");
                }

                _values[name] = args[++i];
            }
        }

        internal string? Optional(string name)
        {
            if (_values.TryGetValue(name, out string? value))
            {
                return value;
            }
            else
            {
                return null;
            }
        }

        internal string Require(string name)
        {
            if (_values.TryGetValue(name, out string? value))
            {
                return value;
            }
            else
            {
                throw new ArgumentException($"--{name} is required");
            }
        }

        internal long RequireLong(string name) => long.Parse(Require(name));

        internal long? OptionalLong(string name)
        {
            if (_values.TryGetValue(name, out string? value))
            {
                return long.Parse(value);
            }
            else
            {
                return null;
            }
        }
    }
}
