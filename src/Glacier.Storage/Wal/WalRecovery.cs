namespace Glacier.Storage.Wal;

using System;
using System.Collections.Generic;
using System.IO;

public sealed class WalRecoveryResult
{
    public ulong MaxLsn { get; set; }
    public HashSet<TxId> CommittedTransactions { get; set; } = new();
    public HashSet<TxId> AbortedTransactions { get; set; } = new();
    public List<WalRecord> CommittedRecords { get; set; } = new();
}

public static class WalRecovery
{
    public static WalRecoveryResult Recover(Stream logStream)
    {
        var result = new WalRecoveryResult();
        if (logStream.Length == 0) return result;

        logStream.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[logStream.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int r = logStream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (r == 0) break;
            totalRead += r;
        }

        ReadOnlySpan<byte> span = buffer.AsSpan(0, totalRead);
        var allRecords = new List<WalRecord>();
        var txStatus = new Dictionary<TxId, WalRecordType>();
        int offset = 0;
        ulong maxLsn = 0;

        while (offset < span.Length)
        {
            ReadOnlySpan<byte> remaining = span.Slice(offset);
            if (!WalRecord.TryDeserialize(remaining, out WalRecord record, out int bytesConsumed))
            {
                // Reached end of valid log or torn block from crash
                break;
            }

            allRecords.Add(record);
            offset += bytesConsumed;
            if (record.Lsn > maxLsn)
            {
                maxLsn = record.Lsn;
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
            else if (!txStatus.ContainsKey(record.TxId))
            {
                txStatus[record.TxId] = WalRecordType.Begin;
            }
        }

        result.MaxLsn = maxLsn;

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
}
