namespace Glacier.Storage.Arrow;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

public sealed class ArrowStreamReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private ArrowSchema? _schema;
    private bool _isDisposed;
    private bool _reachedEos;

    public ArrowSchema? Schema => _schema;
    public bool HasMoreBatches => !_reachedEos;

    public ArrowStreamReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }

    public ArrowSchema ReadSchema()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_schema != null) return _schema;

        byte[] headerBytes = ReadExact(8);
        if (!ArrowIpcParser.TryReadPrefix(headerBytes, out int metaLen))
        {
            throw new InvalidDataException("Failed to read Arrow IPC schema header prefix.");
        }

        if (metaLen == 0)
        {
            _reachedEos = true;
            throw new InvalidDataException("Unexpected end-of-stream before schema message.");
        }

        int alignedMeta = (metaLen + 7) & ~7;
        byte[] metaBytes = ReadExact(alignedMeta);

        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(metaBytes.AsSpan(12, 4));
        ReadOnlySpan<byte> payload = metaBytes.AsSpan(16, payloadLen);

        _schema = ParseSchema(payload);
        return _schema;
    }

    public ArrowRecordBatch? ReadNextRecordBatch()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_reachedEos) return null;

        if (_schema == null)
        {
            ReadSchema();
        }

        byte[] headerBytes = ReadExact(8);
        if (headerBytes.Length < 8)
        {
            _reachedEos = true;
            return null;
        }

        if (!ArrowIpcParser.TryReadPrefix(headerBytes, out int metaLen))
        {
            _reachedEos = true;
            return null;
        }

        if (metaLen == 0)
        {
            _reachedEos = true;
            return null;
        }

        int alignedMeta = (metaLen + 7) & ~7;
        byte[] metaBytes = ReadExact(alignedMeta);

        long bodyLen = BinaryPrimitives.ReadInt64LittleEndian(metaBytes.AsSpan(4, 8));
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(metaBytes.AsSpan(12, 4));
        ReadOnlySpan<byte> metaPayload = metaBytes.AsSpan(16, payloadLen);

        // Read entire body into memory buffer
        byte[] bodyBytes = ReadExact((int)bodyLen);
        ReadOnlyMemory<byte> bodyMemory = bodyBytes;

        if (metaPayload.Length < 8)
        {
            throw new InvalidDataException("Invalid RecordBatch metadata payload.");
        }

        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(metaPayload[..4]);
        int colCount = BinaryPrimitives.ReadInt32LittleEndian(metaPayload.Slice(4, 4));

        var columns = new List<ArrowColumn>(colCount);
        int descOffset = 8;

        for (int i = 0; i < colCount; i++)
        {
            var desc = ArrowColumnDescriptor.ReadFrom(metaPayload.Slice(descOffset, ArrowColumnDescriptor.Size));
            descOffset += ArrowColumnDescriptor.Size;

            var field = _schema!.GetField(desc.FieldIndex);

            ReadOnlyMemory<byte> nullBitmap = desc.NullBitmapLength > 0
                ? bodyMemory.Slice(desc.NullBitmapOffset, desc.NullBitmapLength)
                : ReadOnlyMemory<byte>.Empty;

            ReadOnlyMemory<byte> offsets = desc.OffsetsLength > 0
                ? bodyMemory.Slice(desc.OffsetsOffset, desc.OffsetsLength)
                : ReadOnlyMemory<byte>.Empty;

            ReadOnlyMemory<byte> data = desc.DataLength > 0
                ? bodyMemory.Slice(desc.DataOffset, desc.DataLength)
                : ReadOnlyMemory<byte>.Empty;

            columns.Add(new ArrowColumn(field, desc.Length, desc.NullCount, nullBitmap, offsets, data));
        }

        return new ArrowRecordBatch(_schema!, rowCount, columns);
    }

    private static ArrowSchema ParseSchema(ReadOnlySpan<byte> payload)
    {
        var fields = new List<ArrowField>();
        if (payload.Length < 2)
        {
            fields.Add(new ArrowField("column0", ArrowType.Int32));
            return new ArrowSchema(fields);
        }

        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        int offset = 2;

        for (int i = 0; i < count && offset < payload.Length; i++)
        {
            byte nameLen = payload[offset++];
            string name = Encoding.UTF8.GetString(payload.Slice(offset, nameLen));
            offset += nameLen;
            var typeId = (ArrowTypeId)payload[offset++];
            bool isNullable = payload[offset++] != 0;

            fields.Add(new ArrowField(name, new ArrowType(typeId), isNullable));
        }

        return new ArrowSchema(fields);
    }

    private byte[] ReadExact(int count)
    {
        var buffer = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int r = _stream.Read(buffer, totalRead, count - totalRead);
            if (r == 0)
            {
                if (totalRead == 0)
                {
                    _reachedEos = true;
                    return [];
                }
                throw new EndOfStreamException($"Expected {count} bytes but read {totalRead}.");
            }
            totalRead += r;
        }
        return buffer;
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
