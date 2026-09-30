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

        using var bodyStream = new MemoryStream();
        var descriptors = new List<ArrowColumnDescriptor>(batch.Columns.Count);

        for (int i = 0; i < batch.Columns.Count; i++)
        {
            var col = batch.Columns[i];

            int nullOffset = 0;
            int nullLen = 0;
            if (col.Field.IsNullable && !col.NullBitmap.IsEmpty)
            {
                nullOffset = (int)bodyStream.Position;
                bodyStream.Write(col.NullBitmap.Span);
                nullLen = (int)bodyStream.Position - nullOffset;
                PadStreamTo8(bodyStream);
            }

            int offOffset = 0;
            int offLen = 0;
            if (!col.OffsetsBuffer.IsEmpty)
            {
                offOffset = (int)bodyStream.Position;
                bodyStream.Write(col.OffsetsBuffer.Span);
                offLen = (int)bodyStream.Position - offOffset;
                PadStreamTo8(bodyStream);
            }

            int dataOffset = 0;
            int dataLen = 0;
            if (!col.DataBuffer.IsEmpty)
            {
                dataOffset = (int)bodyStream.Position;
                bodyStream.Write(col.DataBuffer.Span);
                dataLen = (int)bodyStream.Position - dataOffset;
                PadStreamTo8(bodyStream);
            }

            descriptors.Add(new ArrowColumnDescriptor(
                i, col.Length, col.NullCount,
                nullOffset, nullLen,
                offOffset, offLen,
                dataOffset, dataLen));
        }

        byte[] bodyBytes = bodyStream.ToArray();

        int metaSize = 8 + (descriptors.Count * ArrowColumnDescriptor.Size);
        byte[] metaPayload = new byte[metaSize];
        BinaryPrimitives.WriteInt32LittleEndian(metaPayload.AsSpan(0, 4), batch.RowCount);
        BinaryPrimitives.WriteInt32LittleEndian(metaPayload.AsSpan(4, 4), descriptors.Count);

        for (int i = 0; i < descriptors.Count; i++)
        {
            descriptors[i].WriteTo(metaPayload.AsSpan(8 + (i * ArrowColumnDescriptor.Size), ArrowColumnDescriptor.Size));
        }

        WriteFrame(ArrowMessageType.RecordBatch, metaPayload, bodyBytes);
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
