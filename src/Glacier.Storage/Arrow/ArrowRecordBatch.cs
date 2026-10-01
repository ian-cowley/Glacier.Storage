namespace Glacier.Storage.Arrow;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

public sealed class ArrowColumn
{
    public ArrowField Field { get; }
    public int Length { get; }
    public int NullCount { get; }

    public ReadOnlyMemory<byte> NullBitmap { get; }
    public ReadOnlyMemory<byte> OffsetsBuffer { get; }
    public ReadOnlyMemory<byte> DataBuffer { get; }

    public ArrowColumn(
        ArrowField field,
        int length,
        int nullCount,
        ReadOnlyMemory<byte> nullBitmap,
        ReadOnlyMemory<byte> offsetsBuffer,
        ReadOnlyMemory<byte> dataBuffer)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Length = length;
        NullCount = nullCount;
        NullBitmap = nullBitmap;
        OffsetsBuffer = offsetsBuffer;
        DataBuffer = dataBuffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsNull(int index)
    {
        if (NullCount == 0 || NullBitmap.IsEmpty) return false;
        int byteIdx = index >> 3;
        int bitIdx = index & 7;
        return (NullBitmap.Span[byteIdx] & (1 << bitIdx)) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> AsSpan<T>() where T : unmanaged
    {
        return MemoryMarshal.Cast<byte, T>(DataBuffer.Span[..(Length * Unsafe.SizeOf<T>())]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetInt32(int index)
    {
        ReadOnlySpan<byte> span = DataBuffer.Span.Slice(index * sizeof(int), sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt64(int index)
    {
        ReadOnlySpan<byte> span = DataBuffer.Span.Slice(index * sizeof(long), sizeof(long));
        return BinaryPrimitives.ReadInt64LittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetFloat(int index)
    {
        ReadOnlySpan<byte> span = DataBuffer.Span.Slice(index * sizeof(float), sizeof(float));
        return BinaryPrimitives.ReadSingleLittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetDouble(int index)
    {
        ReadOnlySpan<byte> span = DataBuffer.Span.Slice(index * sizeof(double), sizeof(double));
        return BinaryPrimitives.ReadDoubleLittleEndian(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetBoolean(int index)
    {
        int byteIdx = index >> 3;
        int bitIdx = index & 7;
        return (DataBuffer.Span[byteIdx] & (1 << bitIdx)) != 0;
    }

    public string GetString(int index)
    {
        var bytes = GetBytes(index);
        return bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    public ReadOnlySpan<byte> GetBytes(int index)
    {
        if (OffsetsBuffer.IsEmpty) return ReadOnlySpan<byte>.Empty;

        ReadOnlySpan<int> offsets = MemoryMarshal.Cast<byte, int>(OffsetsBuffer.Span);
        int start = offsets[index];
        int end = offsets[index + 1];
        int len = end - start;

        return DataBuffer.Span.Slice(start, len);
    }

    public ArrowColumn Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > Length)
            throw new ArgumentOutOfRangeException();

        // Calculate sliced buffers
        if (Field.DataType.Id == ArrowTypeId.Utf8 || Field.DataType.Id == ArrowTypeId.Binary)
        {
            ReadOnlySpan<int> offsets = MemoryMarshal.Cast<byte, int>(OffsetsBuffer.Span);
            int dataStart = offsets[offset];
            int dataEnd = offsets[offset + length];
            int dataLen = dataEnd - dataStart;

            // Re-base offsets
            var newOffsets = new int[length + 1];
            for (int i = 0; i <= length; i++)
            {
                newOffsets[i] = offsets[offset + i] - dataStart;
            }
            byte[] newOffsetsBytes = MemoryMarshal.AsBytes(newOffsets.AsSpan()).ToArray();
            ReadOnlyMemory<byte> slicedData = DataBuffer.Slice(dataStart, dataLen);

            return new ArrowColumn(Field, length, 0, ReadOnlyMemory<byte>.Empty, newOffsetsBytes, slicedData);
        }
        else
        {
            int elemSize = Field.DataType.ByteWidth;
            int byteStart = offset * elemSize;
            int byteLen = length * elemSize;
            ReadOnlyMemory<byte> slicedData = DataBuffer.Slice(byteStart, byteLen);

            return new ArrowColumn(Field, length, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, slicedData);
        }
    }
}

public sealed class ArrowRecordBatch
{
    public ArrowSchema Schema { get; }
    public int RowCount { get; }
    public int Length => RowCount;
    public int ColumnCount => Columns.Count;
    public IReadOnlyList<ArrowColumn> Columns { get; }

    public ArrowRecordBatch(ArrowSchema schema, int rowCount, IEnumerable<ArrowColumn> columns)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        RowCount = rowCount;
        Columns = new List<ArrowColumn>(columns);
    }

    public ArrowColumn Column(int index) => Columns[index];
    public ArrowColumn Column(string name)
    {
        int idx = Schema.GetFieldIndex(name);
        if (idx < 0) throw new KeyNotFoundException($"Column '{name}' not found in schema.");
        return Columns[idx];
    }

    public ArrowRecordBatch Slice(int offset, int length)
    {
        var slicedCols = new List<ArrowColumn>(Columns.Count);
        for (int i = 0; i < Columns.Count; i++)
        {
            slicedCols.Add(Columns[i].Slice(offset, length));
        }
        return new ArrowRecordBatch(Schema, length, slicedCols);
    }
}
