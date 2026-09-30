namespace Glacier.Storage.Arrow;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Arrow IPC column buffer slice descriptor.
/// </summary>
public readonly struct ArrowColumnDescriptor
{
    public readonly int FieldIndex;
    public readonly int Length;
    public readonly int NullCount;
    public readonly int NullBitmapOffset;
    public readonly int NullBitmapLength;
    public readonly int OffsetsOffset;
    public readonly int OffsetsLength;
    public readonly int DataOffset;
    public readonly int DataLength;

    public ArrowColumnDescriptor(
        int fieldIndex,
        int length,
        int nullCount,
        int nullBitmapOffset,
        int nullBitmapLength,
        int offsetsOffset,
        int offsetsLength,
        int dataOffset,
        int dataLength)
    {
        FieldIndex = fieldIndex;
        Length = length;
        NullCount = nullCount;
        NullBitmapOffset = nullBitmapOffset;
        NullBitmapLength = nullBitmapLength;
        OffsetsOffset = offsetsOffset;
        OffsetsLength = offsetsLength;
        DataOffset = dataOffset;
        DataLength = dataLength;
    }

    public const int Size = 36; // 9 * 4 bytes

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[..4], FieldIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(4, 4), Length);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(8, 4), NullCount);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(12, 4), NullBitmapOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(16, 4), NullBitmapLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(20, 4), OffsetsOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(24, 4), OffsetsLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(28, 4), DataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(32, 4), DataLength);
    }

    public static ArrowColumnDescriptor ReadFrom(ReadOnlySpan<byte> source)
    {
        return new ArrowColumnDescriptor(
            BinaryPrimitives.ReadInt32LittleEndian(source[..4]),
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(8, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(12, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(16, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(20, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(24, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(28, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(32, 4))
        );
    }
}
