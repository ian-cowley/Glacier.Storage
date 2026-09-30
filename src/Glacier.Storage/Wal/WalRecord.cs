namespace Glacier.Storage.Wal;

using System;
using System.Buffers.Binary;

public enum WalRecordType : byte
{
    Begin = 1,
    Update = 2,
    Commit = 3,
    Abort = 4,
    Checkpoint = 5
}

public readonly struct WalRecord
{
    public const uint Magic = 0x57414C31; // "WAL1"
    public const int HeaderSize = 25; // Magic(4) + Lsn(8) + TxId(8) + Type(1) + PayloadLen(4)
    public const int FooterSize = 4; // Checksum(4)

    public readonly ulong Lsn;
    public readonly TxId TxId;
    public readonly WalRecordType Type;
    public readonly ReadOnlyMemory<byte> Payload;
    public readonly uint Checksum;

    public WalRecord(ulong lsn, TxId txId, WalRecordType type, ReadOnlyMemory<byte> payload, uint checksum)
    {
        Lsn = lsn;
        TxId = txId;
        Type = type;
        Payload = payload;
        Checksum = checksum;
    }

    public static int GetSerializedSize(int payloadLength) => HeaderSize + payloadLength + FooterSize;

    public static int Serialize(ulong lsn, TxId txId, WalRecordType type, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        int totalSize = HeaderSize + payload.Length + FooterSize;
        if (destination.Length < totalSize)
        {
            throw new ArgumentException("Destination span too small.", nameof(destination));
        }

        // Header
        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(4, 8), lsn);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(12, 8), txId.Value);
        destination[20] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(21, 4), payload.Length);

        // Payload
        if (!payload.IsEmpty)
        {
            payload.CopyTo(destination.Slice(HeaderSize, payload.Length));
        }

        // Checksum over Header + Payload
        int dataLen = HeaderSize + payload.Length;
        uint crc = Crc32c.Compute(destination[..dataLen]);

        // Footer
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(dataLen, 4), crc);
        return totalSize;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> source, out WalRecord record, out int bytesConsumed)
    {
        record = default;
        bytesConsumed = 0;

        if (source.Length < HeaderSize + FooterSize)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        if (magic != Magic)
        {
            return false;
        }

        ulong lsn = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(4, 8));
        ulong txVal = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(12, 8));
        var type = (WalRecordType)source[20];
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(21, 4));

        if (payloadLen < 0 || source.Length < HeaderSize + payloadLen + FooterSize)
        {
            return false;
        }

        int dataLen = HeaderSize + payloadLen;
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(dataLen, 4));
        uint actualCrc = Crc32c.Compute(source[..dataLen]);

        if (expectedCrc != actualCrc)
        {
            return false;
        }

        ReadOnlyMemory<byte> payloadMem = payloadLen > 0 ? source.Slice(HeaderSize, payloadLen).ToArray() : ReadOnlyMemory<byte>.Empty;
        record = new WalRecord(lsn, new TxId(txVal), type, payloadMem, expectedCrc);
        bytesConsumed = dataLen + FooterSize;
        return true;
    }
}
