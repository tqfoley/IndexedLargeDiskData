using System.Buffers.Binary;

namespace IndexedLargeDiskData.Indexing;

/// <summary>One index entry: a 64-bit key and the ordinal of the record it points at.</summary>
/// <param name="Key">The indexed value, or a hash prefix of it.</param>
/// <param name="Ordinal">The ordinal of the record in its <see cref="Storage.RecordStore{T}"/>.</param>
/// <remarks>
/// Sorted by key then ordinal, so duplicates of a key form one contiguous run with ascending
/// ordinals: a lookup finds the first entry and walks forward until the key changes.
/// </remarks>
internal readonly record struct IndexEntry(ulong Key, ulong Ordinal) : IComparable<IndexEntry>
{
    /// <summary>The on-disk size of an entry.</summary>
    internal const int Size = 16;

    /// <inheritdoc />
    public int CompareTo(IndexEntry other)
    {
        int c = Key.CompareTo(other.Key);
        if (c != 0)
        {
            return c;
        }
        else
        {
            return Ordinal.CompareTo(other.Ordinal);
        }
    }

    internal static IndexEntry Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(source),
        BinaryPrimitives.ReadUInt64LittleEndian(source[8..]));

    internal static void Write(Span<byte> destination, in IndexEntry entry)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, entry.Key);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], entry.Ordinal);
    }
}
