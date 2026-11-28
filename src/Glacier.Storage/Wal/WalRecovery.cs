namespace Glacier.Storage.Wal;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Result of scanning a write-ahead log stream for crash recovery.
/// </summary>
public sealed class WalRecoveryResult
{
    /// <summary>Gets or sets the maximum log sequence number observed in valid records.</summary>
    public ulong MaxLsn { get; set; }

    /// <summary>Gets or sets the maximum transaction identifier observed in valid records.</summary>
    public ulong MaxTxId { get; set; }

    /// <summary>Gets or sets the set of committed transaction identifiers.</summary>
    public HashSet<TxId> CommittedTransactions { get; set; } = new();

    /// <summary>Gets or sets the set of explicitly aborted or incomplete transaction identifiers.</summary>
    public HashSet<TxId> AbortedTransactions { get; set; } = new();

    /// <summary>Gets or sets the committed update records in log order.</summary>
    public List<WalRecord> CommittedRecords { get; set; } = new();
}

/// <summary>
/// Crash-resilient WAL recovery scanner supporting pooled streaming reads and torn-tail isolation.
/// </summary>
public static class WalRecovery
{
    /// <summary>
    /// Scans <paramref name="logStream"/> from the beginning, validates CRC-32C checksums,
    /// stops cleanly at any torn/corrupt tail, and returns committed update records in LSN order.
    /// </summary>
    /// <param name="logStream">Readable WAL stream.</param>
    /// <returns>The <see cref="WalRecoveryResult"/> containing committed and aborted transactions.</returns>
    public static WalRecoveryResult Recover(Stream logStream)
    {
        ArgumentNullException.ThrowIfNull(logStream);

        var result = new WalRecoveryResult();
        long streamLength = logStream.CanSeek ? logStream.Length : -1;
        if (streamLength == 0)
        {
            return result;
        }

        FileStream? fs = logStream as FileStream;
        if (fs == null && logStream.CanSeek)
        {
            logStream.Seek(0, SeekOrigin.Begin);
        }
        else if (fs != null && fs.CanSeek)
        {
            fs.Flush();
        }

        int initialCap = streamLength > 0 && streamLength < 65536
            ? Math.Max(4096, (int)streamLength)
            : 65536;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(initialCap);

        try
        {
            int validBytes = 0;
            long fileOffset = 0;
            bool eof = false;

            var allRecords = new List<WalRecord>();
            var txStatus = new Dictionary<TxId, WalRecordType>();
            ulong maxLsn = 0;
            ulong maxTxId = 0;

            while (true)
            {
                if (!eof && validBytes < buffer.Length)
                {
                    int space = buffer.Length - validBytes;
                    int read;
                    if (fs != null && fs.CanSeek)
                    {
                        read = RandomAccess.Read(fs.SafeFileHandle, buffer.AsSpan(validBytes, space), fileOffset);
                        fileOffset += read;
                    }
                    else
                    {
                        read = logStream.ReadAtLeast(buffer.AsSpan(validBytes, space), space, throwOnEndOfStream: false);
                    }

                    validBytes += read;
                    if (read < space)
                    {
                        eof = true;
                    }
                }

                int parsedOffset = 0;
                bool needMoreBytes = false;

                while (parsedOffset < validBytes)
                {
                    ReadOnlySpan<byte> remaining = buffer.AsSpan(parsedOffset, validBytes - parsedOffset);
                    if (!WalRecord.TryDeserialize(remaining, out WalRecord record, out int bytesConsumed))
                    {
                        if (!eof && IsPotentiallyIncompleteRecord(remaining, out int neededBytes))
                        {
                            needMoreBytes = true;
                            int leftoverBytes = validBytes - parsedOffset;
                            if (neededBytes > buffer.Length)
                            {
                                byte[] grown = ArrayPool<byte>.Shared.Rent(neededBytes);
                                Buffer.BlockCopy(buffer, parsedOffset, grown, 0, leftoverBytes);
                                ArrayPool<byte>.Shared.Return(buffer);
                                buffer = grown;
                                validBytes = leftoverBytes;
                                parsedOffset = 0;
                            }
                        }
                        break;
                    }

                    allRecords.Add(record);
                    parsedOffset += bytesConsumed;

                    if (record.Lsn > maxLsn)
                    {
                        maxLsn = record.Lsn;
                    }
                    if (record.TxId.Value > maxTxId)
                    {
                        maxTxId = record.TxId.Value;
                    }

                    if (record.Type == WalRecordType.Commit)
                    {
                        txStatus[record.TxId] = WalRecordType.Commit;
                        result.CommittedTransactions.Add(record.TxId);
                    }
                    else if (record.Type == WalRecordType.Abort)
                    {
                        txStatus[record.TxId] = WalRecordType.Abort;
                        result.AbortedTransactions.Add(record.TxId);
                    }
                    else if (record.Type != WalRecordType.Checkpoint && !txStatus.ContainsKey(record.TxId))
                    {
                        txStatus[record.TxId] = WalRecordType.Begin;
                    }
                }

                if (needMoreBytes && !eof)
                {
                    int leftover = validBytes - parsedOffset;
                    if (parsedOffset > 0 && leftover > 0)
                    {
                        Buffer.BlockCopy(buffer, parsedOffset, buffer, 0, leftover);
                    }
                    validBytes = leftover;
                    continue;
                }

                if (!eof && parsedOffset == validBytes)
                {
                    validBytes = 0;
                    continue;
                }

                break;
            }

            result.MaxLsn = maxLsn;
            result.MaxTxId = maxTxId;

            // Any transaction without a Commit is treated as aborted/uncommitted
            foreach (var kvp in txStatus)
            {
                if (kvp.Value != WalRecordType.Commit)
                {
                    result.AbortedTransactions.Add(kvp.Key);
                }
            }

            // Filter committed update records in LSN order
            foreach (var rec in allRecords)
            {
                if (result.CommittedTransactions.Contains(rec.TxId) && rec.Type == WalRecordType.Update)
                {
                    result.CommittedRecords.Add(rec);
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsPotentiallyIncompleteRecord(ReadOnlySpan<byte> remaining, out int neededBytes)
    {
        neededBytes = Math.Max(4096, WalRecord.HeaderSize + WalRecord.FooterSize);
        if (remaining.Length < 4)
        {
            return true;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(remaining[..4]);
        if (magic != WalRecord.Magic)
        {
            return false;
        }

        if (remaining.Length < WalRecord.HeaderSize + WalRecord.FooterSize)
        {
            return true;
        }

        byte rawType = remaining[20];
        if (rawType is < (byte)WalRecordType.Begin or > (byte)WalRecordType.Checkpoint)
        {
            return false;
        }

        int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(remaining.Slice(21, 4));
        if (payloadLen < 0)
        {
            return false;
        }

        long total64 = (long)WalRecord.HeaderSize + payloadLen + WalRecord.FooterSize;
        if (total64 > Array.MaxLength)
        {
            return false;
        }

        if (total64 > remaining.Length)
        {
            neededBytes = (int)total64;
            return true;
        }

        return false;
    }
}
