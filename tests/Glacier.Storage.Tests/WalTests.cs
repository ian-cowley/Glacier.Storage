namespace Glacier.Storage.Tests;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Glacier.Storage;
using Glacier.Storage.Wal;
using Xunit;

public class WalTests
{
    [Fact]
    public void Crc32c_KnownVector_Matches()
    {
        // 32-byte test vector "123456789"
        byte[] input = "123456789"u8.ToArray();
        uint crc = Crc32c.Compute(input);
        // Castagnoli CRC32C standard checksum for "123456789" is 0xE3069283
        Assert.Equal(0xE3069283u, crc);
    }

    [Fact]
    public void WalRecord_SerializeDeserialize_PreservesAllFields()
    {
        ulong lsn = 42;
        var txId = new TxId(101);
        var type = WalRecordType.Update;
        byte[] payload = "ColumnarPageUpdatePayload_Data"u8.ToArray();

        Span<byte> buffer = stackalloc byte[WalRecord.GetSerializedSize(payload.Length)];
        int written = WalRecord.Serialize(lsn, txId, type, payload, buffer);

        Assert.True(WalRecord.TryDeserialize(buffer[..written], out var deserialized, out int consumed));
        Assert.Equal(written, consumed);
        Assert.Equal(lsn, deserialized.Lsn);
        Assert.Equal(txId, deserialized.TxId);
        Assert.Equal(type, deserialized.Type);
        Assert.True(deserialized.Payload.Span.SequenceEqual(payload));
    }

    [Fact]
    public void WalRecord_CorruptedChecksum_Rejected()
    {
        byte[] payload = "IntegrityCheckPayload"u8.ToArray();
        byte[] buffer = new byte[WalRecord.GetSerializedSize(payload.Length)];
        WalRecord.Serialize(1, new TxId(1), WalRecordType.Update, payload, buffer);

        // Corrupt 1 byte in payload
        buffer[WalRecord.HeaderSize + 2] ^= 0xFF;

        Assert.False(WalRecord.TryDeserialize(buffer, out _, out _));
    }

    [Fact]
    public async Task WalTransactionEngine_CommitAsync_FlushesAndNotifies()
    {
        using var ms = new MemoryStream();
        using var engine = new WalTransactionEngine(ms, bufferCapacity: 4096, groupCommitIntervalMs: 5, leaveOpen: true);

        var tx1 = engine.BeginTransaction();
        engine.LogRecord(tx1, "Tx1_Data"u8.ToArray());
        await engine.CommitAsync(tx1);

        Assert.True(engine.FlushedLsn > 0);
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public async Task WalRecovery_CrashSimulation_RecoversCommittedIgnoresUncommitted()
    {
        using var ms = new MemoryStream();

        // Transaction 1: committed
        // Transaction 2: aborted
        // Transaction 3: uncommitted (in-flight when crash happened)
        using (var engine = new WalTransactionEngine(ms, bufferCapacity: 65536, groupCommitIntervalMs: 1, leaveOpen: true))
        {
            var tx1 = engine.BeginTransaction();
            engine.LogRecord(tx1, "Update1"u8.ToArray());
            await engine.CommitAsync(tx1);

            var tx2 = engine.BeginTransaction();
            engine.LogRecord(tx2, "Update2_Aborted"u8.ToArray());
            engine.Rollback(tx2);

            var tx3 = engine.BeginTransaction();
            engine.LogRecord(tx3, "Update3_Uncommitted"u8.ToArray());
            // Do NOT commit tx3

            engine.Flush();
        }

        // Simulate crash with partial/corrupted trailing bytes
        ms.Seek(0, SeekOrigin.End);
        ms.WriteByte(0x57); // Partial garbage header
        ms.WriteByte(0x41);
        ms.Flush();

        // Perform recovery
        var recoveryResult = WalRecovery.Recover(ms);

        Assert.Single(recoveryResult.CommittedTransactions);
        Assert.Equal(2, recoveryResult.AbortedTransactions.Count); // tx2 (explicit abort) + tx3 (incomplete)
        Assert.Single(recoveryResult.CommittedRecords);

        string committedText = Encoding.UTF8.GetString(recoveryResult.CommittedRecords[0].Payload.Span);
        Assert.Equal("Update1", committedText);
    }
}
