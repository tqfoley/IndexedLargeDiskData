using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Stores;

/// <summary>
/// The three-value transaction store, indexed on <see cref="TripleRecord.V0"/> and
/// <see cref="TripleRecord.V1"/>.
/// </summary>
/// <remarks>
/// Both indexes are duplicate tolerant: one key normally matches many records, and a lookup returns
/// the whole run in ascending ordinal order.
/// </remarks>
public sealed class TransactionStore : IndexedStore<TripleRecord>
{
    private const int IndexV0 = 0;
    private const int IndexV1 = 1;

    internal TransactionStore(string directory, StoreOptions options, BlockCache cache)
        : base(directory, options, cache, "v0", "v1")
    {
    }

    /// <summary>Appends a transaction and returns its ordinal.</summary>
    public long Append(long v0, long v1, long v2) => Append(new TripleRecord(v0, v1, v2));

    /// <summary>Reads every transaction whose <see cref="TripleRecord.V0"/> equals <paramref name="v0"/>.</summary>
    public IReadOnlyList<TripleRecord> FindByV0(long v0) => FindRecords(IndexV0, v0);

    /// <summary>Reads every transaction whose <see cref="TripleRecord.V1"/> equals <paramref name="v1"/>.</summary>
    public IReadOnlyList<TripleRecord> FindByV1(long v1) => FindRecords(IndexV1, v1);

    /// <summary>Counts the transactions whose <see cref="TripleRecord.V0"/> equals <paramref name="v0"/>.</summary>
    /// <remarks>Does not read the records themselves, so it costs the index lookup and nothing more.</remarks>
    public long CountByV0(long v0) => CountMatching(IndexV0, v0);

    /// <summary>Counts the transactions whose <see cref="TripleRecord.V1"/> equals <paramref name="v1"/>.</summary>
    public long CountByV1(long v1) => CountMatching(IndexV1, v1);

    /// <summary>Gets the ordinals of every transaction whose <see cref="TripleRecord.V0"/> matches.</summary>
    /// <remarks>Cheaper than <see cref="FindByV0"/> when the caller only needs to count or page.</remarks>
    public IReadOnlyList<long> FindOrdinalsByV0(long v0) => FindOrdinals(IndexV0, v0);

    /// <summary>Gets the ordinals of every transaction whose <see cref="TripleRecord.V1"/> matches.</summary>
    public IReadOnlyList<long> FindOrdinalsByV1(long v1) => FindOrdinals(IndexV1, v1);

    /// <inheritdoc />
    protected override long GetKey(int index, in TripleRecord record) => index switch
    {
        IndexV0 => record.V0,
        IndexV1 => record.V1,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}
