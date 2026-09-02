using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Stores;

/// <summary>
/// The transaction store, indexed on the four key fields of a <see cref="QuadrupleRecord"/>.
/// </summary>
/// <remarks>
/// All four indexes are duplicate tolerant: one key normally matches many records, and a lookup
/// returns the whole run in ascending ordinal order. <see cref="QuadrupleRecord.V4"/> has no index
/// of its own; it comes back on the records a lookup returns, but nothing can search for it.
/// </remarks>
public sealed class TransactionStore : IndexedStore<QuadrupleRecord>
{
    private const int IndexV0 = 0;
    private const int IndexV1 = 1;
    private const int IndexV2 = 2;
    private const int IndexV3 = 3;

    internal TransactionStore(string directory, StoreOptions options, BlockCache cache)
        : base(directory, options, cache, "v0", "v1", "v2", "v3")
    {
    }

    /// <summary>Appends a transaction and returns its ordinal.</summary>
    /// <remarks><paramref name="v4"/> is payload: it is stored, but no index is built over it.</remarks>
    public ulong Append(ulong v0, ulong v1, ulong v2, ulong v3, ulong v4) =>
        Append(new QuadrupleRecord(v0, v1, v2, v3, v4));

    /// <summary>Reads every transaction whose <see cref="QuadrupleRecord.V0"/> equals <paramref name="v0"/>.</summary>
    public IReadOnlyList<QuadrupleRecord> FindByV0(ulong v0) => FindRecords(IndexV0, v0);

    /// <summary>Reads every transaction whose <see cref="QuadrupleRecord.V1"/> equals <paramref name="v1"/>.</summary>
    public IReadOnlyList<QuadrupleRecord> FindByV1(ulong v1) => FindRecords(IndexV1, v1);

    /// <summary>Reads every transaction whose <see cref="QuadrupleRecord.V2"/> equals <paramref name="v2"/>.</summary>
    public IReadOnlyList<QuadrupleRecord> FindByV2(ulong v2) => FindRecords(IndexV2, v2);

    /// <summary>Reads every transaction whose <see cref="QuadrupleRecord.V3"/> equals <paramref name="v3"/>.</summary>
    public IReadOnlyList<QuadrupleRecord> FindByV3(ulong v3) => FindRecords(IndexV3, v3);

    /// <summary>Counts the transactions whose <see cref="QuadrupleRecord.V0"/> equals <paramref name="v0"/>.</summary>
    /// <remarks>Does not read the records themselves, so it costs the index lookup and nothing more.</remarks>
    public ulong CountByV0(ulong v0) => CountMatching(IndexV0, v0);

    /// <summary>Counts the transactions whose <see cref="QuadrupleRecord.V1"/> equals <paramref name="v1"/>.</summary>
    public ulong CountByV1(ulong v1) => CountMatching(IndexV1, v1);

    /// <summary>Counts the transactions whose <see cref="QuadrupleRecord.V2"/> equals <paramref name="v2"/>.</summary>
    public ulong CountByV2(ulong v2) => CountMatching(IndexV2, v2);

    /// <summary>Counts the transactions whose <see cref="QuadrupleRecord.V3"/> equals <paramref name="v3"/>.</summary>
    public ulong CountByV3(ulong v3) => CountMatching(IndexV3, v3);

    /// <summary>Gets the ordinals of every transaction whose <see cref="QuadrupleRecord.V0"/> matches.</summary>
    /// <remarks>Cheaper than <see cref="FindByV0"/> when the caller only needs to count or page.</remarks>
    public IReadOnlyList<ulong> FindOrdinalsByV0(ulong v0) => FindOrdinals(IndexV0, v0);

    /// <summary>Gets the ordinals of every transaction whose <see cref="QuadrupleRecord.V1"/> matches.</summary>
    public IReadOnlyList<ulong> FindOrdinalsByV1(ulong v1) => FindOrdinals(IndexV1, v1);

    /// <summary>Gets the ordinals of every transaction whose <see cref="QuadrupleRecord.V2"/> matches.</summary>
    public IReadOnlyList<ulong> FindOrdinalsByV2(ulong v2) => FindOrdinals(IndexV2, v2);

    /// <summary>Gets the ordinals of every transaction whose <see cref="QuadrupleRecord.V3"/> matches.</summary>
    public IReadOnlyList<ulong> FindOrdinalsByV3(ulong v3) => FindOrdinals(IndexV3, v3);

    /// <inheritdoc />
    protected override ulong GetKey(int index, in QuadrupleRecord record)
    {
        // The record has five fields but only four indexes. Asking for a fifth key would silently
        // start indexing the payload, so it is refused here rather than answered.
        if (index >= QuadrupleRecord.IndexedFieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return record.GetField(index);
    }
}
