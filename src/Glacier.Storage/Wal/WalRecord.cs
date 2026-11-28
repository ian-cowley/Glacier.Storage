namespace Glacier.Storage.Wal;

using System;
using System.Buffers.Binary;

/// <summary>
/// Write-ahead log record classification.
/// </summary>
public enum WalRecordType : byte
{
    /// <summary>Transaction begin marker.</summary>
    Begin = 1,

    /// <summary>Transaction data update payload.</summary>
    Update = 2,

    /// <summary>Transaction commit marker.</summary>
    Commit = 3,

    /// <summary>Transaction abort/rollback marker.</summary>
    Abort = 4,

    /// <summary>Buffer pool checkpoint marker.</summary>
    Checkpoint = 5
}

/// <summary>
/// Deserialized write-ahead log record framed with a 25-byte header and 4-byte CRC-32C footer.
/// </summary>
public readonly struct WalRecord
{
    /// <summary>4-byte WAL record magic marker (<c>"WAL1"</c>).</summary>
    public const uint Magic = 0x57414C31; // "WAL1"

    /// <summary>Fixed 25-byte WAL record header length.</summary>
    public const int HeaderSize = 25; // Magic(4) + Lsn(8) + TxId(8) + Type(1) + PayloadLen(4)

    /// <summary>Fixed 4-byte CRC-32C footer length.</summary>
    public const int FooterSize = 4; // Checksum(4)

    /// <summary>Monotonic log sequence number.</summary>
    public readonly ulong Lsn;

    /// <summary>Owning transaction identifier.</summary>
    public readonly TxId TxId;

    /// <summary>Record type.</summary>
    public readonly WalRecordType Type;

    /// <summary>Payload slice.</summary>
    public readonly ReadOnlyMemory<byte> Payload;

    /// <summary>CRC-32C checksum over header and payload.</summary>
    public readonly uint Checksum;

    /// <summary>
    /// Initializes a <see cref="WalRecord"/>.
    /// </summary>
    /// <param name="lsn">Log sequence number.</param>
    /// <param name="txId">Transaction identifier.</param>
    /// <param name="type">WAL record type.</param>
    /// <param name="payload">Payload bytes.</param>
    /// <param name="checksum">CRC-32C checksum.</param>
    public WalRecord(ulong lsn, TxId txId, WalRecordType type, ReadOnlyMemory<byte> payload, uint checksum)
    {
        Lsn = lsn;
        TxId = txId;
        Type = type;
        Payload = payload;
        Checksum = checksum;
    }

    /// <summary>
    /// Computes the serialized byte length of a WAL record for <paramref name="payloadLength"/> payload bytes.
    /// </summary>
    /// <param name="payloadLength">Non-negative payload length in bytes.</param>
    /// <returns>Total serialized record size in bytes.</returns>
    public static int GetSerializedSize(int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        return checked(HeaderSize + payloadLength + FooterSize);
    }

    /// <summary>
    /// Serializes a WAL record with header and CRC-32C footer into <paramref name="destination"/>.
    /// </summary>
    /// <param name="lsn">Log sequence number.</param>
    /// <param name="txId">Transaction identifier.</param>
    /// <param name="type">WAL record type.</param>
    /// <param name="payload">Record payload bytes.</param>
    /// <param name="destination">Destination byte span.</param>
    /// <returns>Total bytes written.</returns>
    public static int Serialize(ulong lsn, TxId txId, WalRecordType type, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        int totalSize = checked(HeaderSize + payload.Length + FooterSize);
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

    /// <summary>
    /// Attempts to deserialize and CRC-verify a WAL record from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Source byte span.</param>
    /// <param name="record">Receives the deserialized record if valid.</param>
    /// <param name="bytesConsumed">Receives the total bytes consumed by the record.</param>
    /// <returns><c>true</c> if valid and CRC-verified; otherwise <c>false</c>.</returns>
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
        byte rawType = source[20];
        if (rawType is < (byte)WalRecordType.Begin or > (byte)WalRecordType.Checkpoint)
        {
            return false;
        }

        var type = (WalRecordType)rawType;
        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(21, 4));

        if (payloadLen < 0 || (long)HeaderSize + payloadLen + FooterSize > source.Length)
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
