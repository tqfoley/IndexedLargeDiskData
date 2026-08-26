using System.Buffers.Binary;

namespace IndexedLargeDiskData.Records;

/// <summary>Three 64-bit values, 24 bytes on disk, indexed on <see cref="V0"/> and <see cref="V1"/>.</summary>
/// <param name="V0">First value. Indexed.</param>
/// <param name="V1">Second value. Indexed.</param>
/// <param name="V2">Third value. Carried as a payload only.</param>
public readonly record struct TripleRecord(long V0, long V1, long V2) : IFixedRecord<TripleRecord>
{
    /// <summary>The on-disk size of a <see cref="TripleRecord"/>.</summary>
    public const int RecordSize = 24;

    /// <inheritdoc />
    public static int Size => RecordSize;

    /// <summary>Gets the field selected by <paramref name="field"/>.</summary>
    /// <param name="field">Zero for <see cref="V0"/>, one for <see cref="V1"/>, two for <see cref="V2"/>.</param>
    public long GetField(int field) => field switch
    {
        0 => V0,
        1 => V1,
        2 => V2,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <inheritdoc />
    public static void Write(Span<byte> destination, in TripleRecord value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.V0);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], value.V1);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], value.V2);
    }

    /// <inheritdoc />
    public static TripleRecord Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadInt64LittleEndian(source),
        BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(source[16..]));
}
