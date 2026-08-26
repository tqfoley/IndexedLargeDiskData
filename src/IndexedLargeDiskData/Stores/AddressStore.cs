using System.Diagnostics.CodeAnalysis;
using IndexedLargeDiskData.Caching;
using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Stores;

/// <summary>
/// The identifier-to-address store, navigable in both directions.
/// </summary>
/// <remarks>
/// The identifier is indexed directly. The address is indexed on its leading
/// <see cref="AddressRecord.PrefixLength"/> characters rather than in full: a 75-character key would
/// make every index entry 83 bytes instead of 16. A prefix match is treated as a candidate and
/// confirmed by comparing the full address on the record, so two addresses that open with the same
/// characters cost one extra read each and never a wrong answer.
/// </remarks>
public sealed class AddressStore : IndexedStore<AddressRecord>
{
    private const int IndexId = 0;
    private const int IndexAddress = 1;

    internal AddressStore(string directory, StoreOptions options, BlockCache cache)
        : base(directory, options, cache, "id", "address")
    {
    }

    /// <summary>Appends an identifier and a 75-character address, returning its ordinal.</summary>
    public long Append(long id, string address) => Append(new AddressRecord(id, address));

    /// <summary>Reads every record carrying <paramref name="id"/>.</summary>
    public IReadOnlyList<AddressRecord> FindById(long id) => FindRecords(IndexId, id);

    /// <summary>Gets the address stored against <paramref name="id"/>.</summary>
    /// <returns>True when a record exists. When several do, the earliest is returned.</returns>
    public bool TryGetAddress(long id, [MaybeNullWhen(false)] out string address)
    {
        IReadOnlyList<AddressRecord> matches = FindById(id);
        if (matches.Count == 0)
        {
            address = null;
            return false;
        }

        address = matches[0].Address;
        return true;
    }

    /// <summary>Reads every record carrying <paramref name="address"/>, confirming prefix candidates.</summary>
    public IReadOnlyList<AddressRecord> FindByAddress(string address)
    {
        AddressRecord.Validate(address);

        List<AddressRecord> confirmed = [];
        foreach (long ordinal in FindOrdinals(IndexAddress, AddressRecord.PrefixOf(address)))
        {
            AddressRecord candidate = Read(ordinal);
            if (string.Equals(candidate.Address, address, StringComparison.Ordinal))
            {
                confirmed.Add(candidate);
            }
        }

        return confirmed;
    }

    /// <summary>Gets the identifier stored against <paramref name="address"/>.</summary>
    /// <returns>True when a record exists. When several do, the earliest is returned.</returns>
    public bool TryGetId(string address, out long id)
    {
        IReadOnlyList<AddressRecord> matches = FindByAddress(address);
        if (matches.Count == 0)
        {
            id = 0;
            return false;
        }

        id = matches[0].Id;
        return true;
    }

    /// <inheritdoc />
    protected override long GetKey(int index, in AddressRecord record) => index switch
    {
        IndexId => record.Id,
        IndexAddress => record.AddressPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}
