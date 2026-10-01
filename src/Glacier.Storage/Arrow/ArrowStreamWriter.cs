namespace Glacier.Storage.Arrow;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

public sealed class ArrowStreamWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _isDisposed;

    public ArrowStreamWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }

    public void WriteSchema(ArrowSchema schema)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write((ushort)schema.FieldCount);
        for (int i = 0; i < schema.FieldCount; i++)
        {
            var field = schema.GetField(i);
            byte[] nameBytes = Encoding.UTF8.GetBytes(field.Name);
            bw.Write((byte)nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write((byte)field.DataType.Id);
            bw.Write(field.IsNullable ? (byte)1 : (byte)0);
        }
        bw.Flush();

        byte[] schemaPayload = ms.ToArray();
        WriteFrame(ArrowMessageType.Schema, schemaPayload, ReadOnlySpan<byte>.Empty);
    }

    public void WriteRecordBatch(ArrowRecordBatch batch)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        int colCount = batch.Columns.Count;
        int metaSize = 8 + (colCount * ArrowColumnDescriptor.Size);
        byte[]? rented = null;
        Span<byte> metaPayload = metaSize <= 1024
            ? stackalloc byte[metaSize]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(metaSize)).AsSpan(0, metaSize);

        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(metaPayload[..4], batch.RowCount);
            BinaryPrimitives.WriteInt32LittleEndian(metaPayload.Slice(4, 4), colCount);

            long currentBodyOffset = 0;
            for (int i = 0; i < colCount; i++)
            {
                var col = batch.Columns[i];

                int nullOffset = 0;
                int nullLen = 0;
                if (col.Field.IsNullable && !col.NullBitmap.IsEmpty)
                {
                    nullOffset = (int)currentBodyOffset;
                    nullLen = col.NullBitmap.Length;
                    currentBodyOffset += nullLen;
                    int pad = (8 - (nullLen % 8)) % 8;
                    currentBodyOffset += pad;
                }

                int offOffset = 0;
                int offLen = 0;
                if (!col.OffsetsBuffer.IsEmpty)
                {
                    offOffset = (int)currentBodyOffset;
                    offLen = col.OffsetsBuffer.Length;
                    currentBodyOffset += offLen;
                    int pad = (8 - (offLen % 8)) % 8;
                    currentBodyOffset += pad;
                }

                int dataOffset = 0;
                int dataLen = 0;
                if (!col.DataBuffer.IsEmpty)
                {
                    dataOffset = (int)currentBodyOffset;
                    dataLen = col.DataBuffer.Length;
                    currentBodyOffset += dataLen;
                    int pad = (8 - (dataLen % 8)) % 8;
                    currentBodyOffset += pad;
                }

                var desc = new ArrowColumnDescriptor(
                    i, col.Length, col.NullCount,
                    nullOffset, nullLen,
                    offOffset, offLen,
                    dataOffset, dataLen);
                desc.WriteTo(metaPayload.Slice(8 + (i * ArrowColumnDescriptor.Size), ArrowColumnDescriptor.Size));
            }

            long totalBodyLength = currentBodyOffset;

            // Combined metadata:
            // [MessageType: 1B] [Reserved: 3B] [BodyLength: 8B] [PayloadLength: 4B] [Payload]
            int metaHeaderLen = 16;
            int totalMetaLen = metaHeaderLen + metaSize;

            Span<byte> frameHeader = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[..4], ArrowIpcParser.ContinuationMarker);
            BinaryPrimitives.WriteInt32LittleEndian(frameHeader.Slice(4, 4), totalMetaLen);
            _stream.Write(frameHeader);

            Span<byte> metaHeader = stackalloc byte[metaHeaderLen];
            metaHeader[0] = (byte)ArrowMessageType.RecordBatch;
            metaHeader[1] = 0;
            metaHeader[2] = 0;
            metaHeader[3] = 0;
            BinaryPrimitives.WriteInt64LittleEndian(metaHeader.Slice(4, 8), totalBodyLength);
            BinaryPrimitives.WriteInt32LittleEndian(metaHeader.Slice(12, 4), metaSize);
            _stream.Write(metaHeader);

            _stream.Write(metaPayload);

            int metaPad = (8 - (totalMetaLen % 8)) % 8;
            Span<byte> zeros = stackalloc byte[8];
            zeros.Clear();
            if (metaPad > 0)
            {
                _stream.Write(zeros[..metaPad]);
            }

            // Stream column buffers directly into destination stream without intermediate heap buffering
            for (int i = 0; i < colCount; i++)
            {
                var col = batch.Columns[i];

                if (col.Field.IsNullable && !col.NullBitmap.IsEmpty)
                {
                    _stream.Write(col.NullBitmap.Span);
                    int pad = (8 - (col.NullBitmap.Length % 8)) % 8;
                    if (pad > 0) _stream.Write(zeros[..pad]);
                }

                if (!col.OffsetsBuffer.IsEmpty)
                {
                    _stream.Write(col.OffsetsBuffer.Span);
                    int pad = (8 - (col.OffsetsBuffer.Length % 8)) % 8;
                    if (pad > 0) _stream.Write(zeros[..pad]);
                }

                if (!col.DataBuffer.IsEmpty)
                {
                    _stream.Write(col.DataBuffer.Span);
                    int pad = (8 - (col.DataBuffer.Length % 8)) % 8;
                    if (pad > 0) _stream.Write(zeros[..pad]);
                }
            }

            _stream.Flush();
        }
        finally
        {
            if (rented != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public void WriteEndOfStream()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        Span<byte> eos = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(eos[..4], ArrowIpcParser.ContinuationMarker);
        BinaryPrimitives.WriteInt32LittleEndian(eos.Slice(4, 4), 0); // 0 indicates EOS
        _stream.Write(eos);
        _stream.Flush();
    }

    private void WriteFrame(ArrowMessageType type, ReadOnlySpan<byte> metadataPayload, ReadOnlySpan<byte> body)
    {
        // Combined metadata:
        // [MessageType: 1B] [Reserved: 3B] [BodyLength: 8B] [PayloadLength: 4B] [Payload]
        int metaHeaderLen = 16;
        int totalMetaLen = metaHeaderLen + metadataPayload.Length;

        Span<byte> frameHeader = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[..4], ArrowIpcParser.ContinuationMarker);
        BinaryPrimitives.WriteInt32LittleEndian(frameHeader.Slice(4, 4), totalMetaLen);
        _stream.Write(frameHeader);

        Span<byte> metaHeader = stackalloc byte[metaHeaderLen];
        metaHeader[0] = (byte)type;
        metaHeader[1] = 0;
        metaHeader[2] = 0;
        metaHeader[3] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(metaHeader.Slice(4, 8), body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(metaHeader.Slice(12, 4), metadataPayload.Length);
        _stream.Write(metaHeader);

        if (!metadataPayload.IsEmpty)
        {
            _stream.Write(metadataPayload);
        }

        int pad = ((8 - (totalMetaLen % 8)) % 8);
        for (int i = 0; i < pad; i++) _stream.WriteByte(0);

        if (!body.IsEmpty)
        {
            _stream.Write(body);
        }

        _stream.Flush();
    }

    private static void PadStreamTo8(Stream stream)
    {
        long pos = stream.Position;
        int rem = (int)(pos % 8);
        if (rem != 0)
        {
            int pad = 8 - rem;
            for (int i = 0; i < pad; i++) stream.WriteByte(0);
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }
}
