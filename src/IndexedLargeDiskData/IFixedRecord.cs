namespace IndexedLargeDiskData;

/// <summary>
/// A record with a compile-time constant on-disk size, addressed by ordinal rather than by offset.
/// </summary>
/// <typeparam name="TSelf">The implementing record type.</typeparam>
/// <remarks>
/// Fixed width is what lets a store skip an offset table entirely: record <c>n</c> lives at
/// <c>n * Size</c>, so an index entry only has to carry an 8-byte ordinal.
/// </remarks>
public interface IFixedRecord<TSelf>
    where TSelf : IFixedRecord<TSelf>
{
    /// <summary>Gets the exact number of bytes one record occupies on disk.</summary>
    static abstract int Size { get; }

    /// <summary>Serializes <paramref name="value"/> into the first <c>Size</c> bytes of <paramref name="destination"/>.</summary>
    static abstract void Write(Span<byte> destination, in TSelf value);

    /// <summary>Deserializes a record from the first <c>Size</c> bytes of <paramref name="source"/>.</summary>
    static abstract TSelf Read(ReadOnlySpan<byte> source);
}
