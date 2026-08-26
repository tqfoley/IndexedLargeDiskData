using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace IndexedLargeDiskData.Records;

/// <summary>A 64-bit identifier paired with a 75-character address, 83 bytes on disk.</summary>
/// <remarks>
/// The address is text of a fixed length, held as a <see cref="string"/> and stored as
/// <see cref="AddressLength"/> ASCII bytes — one byte per character, so a character count and a byte
/// count are the same number and the record keeps the fixed width the whole storage layer depends on.
/// A read allocates the string; that is the cost of the value being text rather than an inline
/// blittable struct.
/// </remarks>
public readonly record struct AddressRecord : IFixedRecord<AddressRecord>
{
    /// <summary>The exact length of an address, in characters, which is also its length in bytes.</summary>
    public const int AddressLength = 75;

    /// <summary>The number of leading characters of an address that make up its index key.</summary>
    public const int PrefixLength = 8;

    /// <summary>The on-disk size of an <see cref="AddressRecord"/>.</summary>
    public const int RecordSize = 8 + AddressLength;

    /// <summary>Creates a record from an identifier and an address.</summary>
    /// <param name="id">The identifier. Indexed.</param>
    /// <param name="address">
    /// The address, exactly <see cref="AddressLength"/> ASCII characters. Indexed on its leading
    /// <see cref="PrefixLength"/> characters.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="address"/> is the wrong length or not ASCII.</exception>
    public AddressRecord(long id, string address)
    {
        Validate(address, nameof(address));
        Id = id;
        Address = address;
    }

    /// <summary>Gets the identifier. Indexed.</summary>
    public long Id { get; }

    /// <summary>Gets the address, exactly <see cref="AddressLength"/> ASCII characters.</summary>
    public string Address { get; }

    /// <summary>Gets the index key derived from <see cref="Address"/>.</summary>
    public long AddressPrefix => PrefixOf(Address);

    /// <inheritdoc />
    public static int Size => RecordSize;

    /// <summary>Reports whether <paramref name="address"/> can be stored as an address.</summary>
    public static bool IsValid(ReadOnlySpan<char> address)
    {
        if (address.Length != AddressLength)
        {
            return false;
        }
        else
        {
            return Ascii.IsValid(address);
        }
    }

    /// <summary>Gets the index key for an address: its leading <see cref="PrefixLength"/> characters.</summary>
    /// <remarks>
    /// A full-width key would make every index entry 83 bytes instead of 16. The prefix is only a
    /// candidate — two addresses may well share their first characters — so any lookup that goes
    /// through this key has to confirm the hit against the full address on the record. If a corpus
    /// arrives where every address opens with the same scheme or network tag, this is the one place
    /// to change: a 64-bit hash of the whole string would spread the keys out, at the cost of the
    /// tests that force a collision deliberately.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="address"/> is shorter than the prefix.</exception>
    public static long PrefixOf(ReadOnlySpan<char> address)
    {
        if (address.Length < PrefixLength)
        {
            throw new ArgumentException("Expected at least " + PrefixLength + " characters.", nameof(address));
        }

        // Matches a little-endian read of the first PrefixLength stored bytes, so the key an index
        // build derives from a record and the key a lookup derives from a query string agree.
        ulong key = 0;
        for (int i = 0; i < PrefixLength; i++)
        {
            key |= (ulong)(byte)address[i] << (i * 8);
        }

        return unchecked((long)key);
    }

    /// <summary>Throws unless <paramref name="address"/> can be stored as an address.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="address"/> is the wrong length or not ASCII.</exception>
    public static void Validate(string? address) => Validate(address, nameof(address));

    /// <inheritdoc />
    public static void Write(Span<byte> destination, in AddressRecord value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.Id);

        string address = value.Address;
        ArgumentNullException.ThrowIfNull(address, nameof(value));
        if (address.Length != AddressLength)
        {
            throw new ArgumentException(
                "Expected exactly " + AddressLength + " characters, got " + address.Length + ".",
                nameof(value));
        }

        // Transcoding is also the ASCII check, so the write path scans the characters once rather
        // than validating them and then copying them.
        if (Ascii.FromUtf16(address, destination.Slice(8, AddressLength), out _) != OperationStatus.Done)
        {
            throw new ArgumentException("The address must be ASCII.", nameof(value));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Decoded as Latin-1 rather than ASCII on purpose: every byte maps to a distinct character, so a
    /// stored byte that is not ASCII fails the constructor's check instead of being folded into a
    /// <c>'?'</c> and handed back as a plausible address. Nothing this type writes is outside ASCII,
    /// so that path only opens on a corrupt record.
    /// </remarks>
    public static AddressRecord Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadInt64LittleEndian(source),
        Encoding.Latin1.GetString(source.Slice(8, AddressLength)));

    private static void Validate(string? address, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(address, parameterName);
        if (address.Length != AddressLength)
        {
            throw new ArgumentException(
                "Expected exactly " + AddressLength + " characters, got " + address.Length + ".",
                parameterName);
        }

        if (!Ascii.IsValid(address))
        {
            throw new ArgumentException("The address must be ASCII.", parameterName);
        }
    }
}
