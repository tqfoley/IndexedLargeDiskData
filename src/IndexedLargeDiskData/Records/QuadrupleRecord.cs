using System.Buffers.Binary;

namespace IndexedLargeDiskData.Records;

/// <summary>
/// Five 64-bit values, 40 bytes on disk: the first four indexed, the fifth carried as payload.
/// </summary>
/// <param name="V0">First value. Indexed.</param>
/// <param name="V1">Second value. Indexed.</param>
/// <param name="V2">Third value. Indexed.</param>
/// <param name="V3">Fourth value. Indexed.</param>
/// <param name="V4">Fifth value. Payload only: no index is built over it.</param>
/// <remarks>
/// The name follows the four indexed values; <see cref="V4"/> rides along on the record and is only
/// ever read back through one of them. That is what makes it cheap — an indexed field costs a
/// 16-byte index entry per record on top of its 8 bytes here, while a payload field costs 8 bytes
/// and nothing else. Put a value in <see cref="V4"/> when queries return it but never start from it.
/// </remarks>
public readonly record struct QuadrupleRecord(ulong V0, ulong V1, ulong V2, ulong V3, ulong V4)
    : IFixedRecord<QuadrupleRecord>
{
    /// <summary>The on-disk size of a <see cref="QuadrupleRecord"/>.</summary>
    public const int RecordSize = 40;

    /// <summary>The number of values a record carries, indexed and payload together.</summary>
    public const int FieldCount = 5;

    /// <summary>How many of the leading fields have an index built over them.</summary>
    public const int IndexedFieldCount = 4;

    /// <inheritdoc />
    public static int Size => RecordSize;

    /// <summary>Gets the field selected by <paramref name="field"/>.</summary>
    /// <param name="field">
    /// Zero for <see cref="V0"/> through four for <see cref="V4"/>. Only the first
    /// <see cref="IndexedFieldCount"/> of those are keys; four selects the payload.
    /// </param>
    public ulong GetField(int field) => field switch
    {
        0 => V0,
        1 => V1,
        2 => V2,
        3 => V3,
        4 => V4,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <inheritdoc />
    public static void Write(Span<byte> destination, in QuadrupleRecord value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, value.V0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], value.V1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], value.V2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], value.V3);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], value.V4);
    }

    /// <inheritdoc />
    public static QuadrupleRecord Read(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(source),
        BinaryPrimitives.ReadUInt64LittleEndian(source[8..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[16..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[24..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[32..]));
}
