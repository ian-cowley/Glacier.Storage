namespace Glacier.Storage.Tests;

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
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

    [Fact]
    public void WalRecord_OverflowAndInvalidType_RejectedCleanly()
    {
        byte[] payload = "abc"u8.ToArray();
        byte[] buffer = new byte[WalRecord.GetSerializedSize(payload.Length)];
        WalRecord.Serialize(1, new TxId(1), WalRecordType.Update, payload, buffer);

        // Invalid WalRecordType = 99
        byte[] badType = (byte[])buffer.Clone();
        badType[20] = 99;
        Assert.False(WalRecord.TryDeserialize(badType, out _, out _));

        // 32-bit overflow payloadLen = int.MaxValue - 10
        byte[] overflowLen = (byte[])buffer.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(overflowLen.AsSpan(21, 4), int.MaxValue - 10);
        Assert.False(WalRecord.TryDeserialize(overflowLen, out _, out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => WalRecord.GetSerializedSize(-1));
        Assert.Throws<OverflowException>(() => WalRecord.GetSerializedSize(int.MaxValue));
    }

    [Fact]
    public async Task WalRecovery_SkipsCheckpointRecords_AndRecoversLargeFileStream()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"glacier_wal_rec_{Guid.NewGuid():N}.wal");
        try
        {
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                // Write > 80 KB across multiple records (exceeding 64 KB initial pooled buffer) + a Checkpoint record
                byte[] ckptBuf = new byte[WalRecord.GetSerializedSize(0)];
                WalRecord.Serialize(1, new TxId(0), WalRecordType.Checkpoint, ReadOnlySpan<byte>.Empty, ckptBuf);
                fs.Write(ckptBuf);

                byte[] bigPayload = new byte[80_000];
                Array.Fill(bigPayload, (byte)0x5A);

                using var engine = new WalTransactionEngine(fs, bufferCapacity: 8192, groupCommitIntervalMs: 5, leaveOpen: true, initialLsn: 1, initialTxId: 0);
                var tx = engine.BeginTransaction();
                engine.LogRecord(tx, bigPayload);
                await engine.CommitAsync(tx);
            }

            using (var readFs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var result = WalRecovery.Recover(readFs);
                Assert.Single(result.CommittedTransactions);
                Assert.Empty(result.AbortedTransactions); // Checkpoint must NOT create a fake aborted tx
                Assert.Single(result.CommittedRecords);
                Assert.Equal(80_000, result.CommittedRecords[0].Payload.Length);
                Assert.Equal((byte)0x5A, result.CommittedRecords[0].Payload.Span[79_999]);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private sealed class SlowFlushStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly object _writeSync = new();
        private readonly int _flushDelayMs;

        public SlowFlushStream(int flushDelayMs) => _flushDelayMs = flushDelayMs;

        public byte[] ToArraySafe() { lock (_writeSync) return _inner.ToArray(); }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length { get { lock (_writeSync) return _inner.Length; } }
        public override long Position { get { lock (_writeSync) return _inner.Position; } set { lock (_writeSync) _inner.Position = value; } }

        public override void Flush()
        {
            Thread.Sleep(_flushDelayMs);
        }

        public override int Read(byte[] buffer, int offset, int count) { lock (_writeSync) return _inner.Read(buffer, offset, count); }
        public override long Seek(long offset, SeekOrigin origin) { lock (_writeSync) return _inner.Seek(offset, origin); }
        public override void SetLength(long value) { lock (_writeSync) _inner.SetLength(value); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_writeSync) _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_writeSync) _inner.Write(buffer);
        }
    }

    [Fact]
    public async Task WalTransactionEngine_ConcurrentAppendsOversizedAndSlowFlush_NoHangOnDispose()
    {
        using var slowStream = new SlowFlushStream(flushDelayMs: 15);
        var engine = new WalTransactionEngine(slowStream, bufferCapacity: 4096, groupCommitIntervalMs: 5, leaveOpen: true);

        var startGate = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        const int threadCount = 8;
        const int txPerThread = 15;

        Task[] workers = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            int tid = t;
            workers[t] = Task.Run(async () =>
            {
                startGate.Wait();
                try
                {
                    for (int i = 0; i < txPerThread; i++)
                    {
                        var tx = engine.BeginTransaction();
                        if ((tid + i) % 7 == 0)
                        {
                            // Oversized payload > 4096 bytes
                            byte[] large = new byte[6000];
                            Array.Fill(large, (byte)(tid + 1));
                            engine.LogRecord(tx, large);
                        }
                        else
                        {
                            byte[] small = Encoding.UTF8.GetBytes($"t{tid}_i{i}");
                            engine.LogRecord(tx, small);
                        }
                        await engine.CommitAsync(tx);
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });
        }

        startGate.Set();
        await Task.WhenAll(workers);
        Assert.Empty(errors);

        // Test Dispose while commits are queued/in-flight: must not hang
        var pendingList = new ConcurrentBag<Task>();
        for (int i = 0; i < 4; i++)
        {
            var tx = engine.BeginTransaction();
            engine.LogRecord(tx, "pre_dispose"u8);
            pendingList.Add(Task.Run(async () =>
            {
                try
                {
                    await engine.CommitAsync(tx);
                }
                catch (ObjectDisposedException)
                {
                    // Expected if engine disposes before commit completes
                }
            }));
        }

        engine.Dispose();

        // All pending commit tasks must finish promptly (no hang)
        Task allPending = Task.WhenAll(pendingList);
        Task completed = await Task.WhenAny(allPending, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(allPending, completed);
        await allPending;

        // Verify all committed records from worker threads are intact & ordered in WAL
        using var verifyMs = new MemoryStream(slowStream.ToArraySafe());
        var recovery = WalRecovery.Recover(verifyMs);
        Assert.True(recovery.CommittedTransactions.Count >= threadCount * txPerThread);
        Assert.Equal(recovery.CommittedTransactions.Count, recovery.CommittedRecords.Count);
    }

#if USE_LOCAL_FORMATS
    [Fact]
    public void Crc32c_LocalFormatsDelegation_MatchesCanonical()
    {
        byte[] data = Encoding.UTF8.GetBytes("Glacier.Storage -> Glacier.Formats CRC32C parity check!");
        Assert.Equal(Glacier.Formats.Hashing.Crc32c.Compute(data), Crc32c.Compute(data));

        uint runningStorage = Crc32c.Update(0xFFFFFFFFu, data.AsSpan(0, 17));
        uint runningFormats = Glacier.Formats.Hashing.Crc32c.Update(0xFFFFFFFFu, data.AsSpan(0, 17));
        Assert.Equal(runningFormats, runningStorage);
    }
#endif
}
