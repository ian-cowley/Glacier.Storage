namespace Glacier.Storage.Tests;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Glacier.Storage;
using Glacier.Storage.BufferPool;
using Glacier.Storage.Wal;
using Glacier.Formats.Arrow;
using Glacier.Formats.Parquet;
using Xunit;

public class TableStoreTests
{
    [Fact]
    public async Task TableStore_AppendCommitExport_WorksEndToEnd()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"glacier_store_test_{Guid.NewGuid():N}");
        try
        {
            var schema = new ArrowSchema(new[]
            {
                new ArrowField("id", ArrowType.Int32),
                new ArrowField("val", ArrowType.Double)
            });

            using (var store = new TableStore(dir, schema))
            {
                var tx = store.BeginTransaction();

                int rows = 100;
                var ids = new int[rows];
                var vals = new double[rows];
                for (int i = 0; i < rows; i++)
                {
                    ids[i] = i;
                    vals[i] = i * 2.5;
                }

                var col0 = new ArrowColumn(schema.GetField(0), rows, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, MemoryMarshal.AsBytes(ids.AsSpan()).ToArray());
                var col1 = new ArrowColumn(schema.GetField(1), rows, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, MemoryMarshal.AsBytes(vals.AsSpan()).ToArray());
                var batch = new ArrowRecordBatch(schema, rows, new[] { col0, col1 });

                store.AppendBatch(tx, batch);
                await store.CommitTransactionAsync(tx);

                Assert.Equal(1, store.BatchCount);

                string parquetExport = Path.Combine(dir, "export.parquet");
                store.ExportToParquet(parquetExport);
                Assert.True(File.Exists(parquetExport));

                var readParquet = ParquetReader.ReadFile(parquetExport);
                Assert.Equal(rows, readParquet.RowCount);
                Assert.Equal(0, readParquet.Column(0).GetInt32(0));
                Assert.Equal(99, readParquet.Column(0).GetInt32(99));
                Assert.Equal(99 * 2.5, readParquet.Column(1).GetDouble(99), 4);

                using var arrowStream = new MemoryStream();
                store.ExportToArrowIpc(arrowStream);
                arrowStream.Seek(0, SeekOrigin.Begin);

                using var arrowReader = new ArrowStreamReader(arrowStream);
                var arrowBatch = arrowReader.ReadNextRecordBatch();
                Assert.NotNull(arrowBatch);
                Assert.Equal(rows, arrowBatch.RowCount);
                Assert.Equal(0, arrowBatch.Column(0).GetInt32(0));
                Assert.Equal(99, arrowBatch.Column(0).GetInt32(99));
            }
        }
        finally
        {
                if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TableStore_UncommittedBatchIsIsolated_AndRestartContinuesTxIdsCleanly()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"glacier_store_reopen_{Guid.NewGuid():N}");
        try
        {
            var schema = new ArrowSchema(new[]
            {
                new ArrowField("id", ArrowType.Int32)
            });

            static ArrowRecordBatch MakeBatch(ArrowSchema s, int idVal)
            {
                int[] ids = [idVal];
                var col = new ArrowColumn(s.GetField(0), 1, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, MemoryMarshal.AsBytes(ids.AsSpan()).ToArray());
                return new ArrowRecordBatch(s, 1, new[] { col });
            }

            // Session 1: commit tx1, leave txUncommitted uncommitted, and verify isolation
            using (var store1 = new TableStore(dir, schema))
            {
                var tx1 = store1.BeginTransaction();
                store1.AppendBatch(tx1, MakeBatch(schema, 10));
                Assert.Equal(0, store1.BatchCount); // uncommitted must not leak into BatchCount
                await store1.CommitTransactionAsync(tx1);
                Assert.Equal(1, store1.BatchCount);

                var txAborted = store1.BeginTransaction();
                store1.AppendBatch(txAborted, MakeBatch(schema, 999));
                store1.RollbackTransaction(txAborted);
                Assert.Equal(1, store1.BatchCount);
            }

            // Session 2: reopen, commit a new tx (must NOT collide with prior TxId(1)), and close
            using (var store2 = new TableStore(dir, schema))
            {
                Assert.Equal(1, store2.BatchCount);
                var txNext = store2.BeginTransaction();
                store2.AppendBatch(txNext, MakeBatch(schema, 20));
                await store2.CommitTransactionAsync(txNext);
                Assert.Equal(2, store2.BatchCount);
            }

            // Session 3: reopen again and confirm both pre-restart and post-restart batches survive
            using (var store3 = new TableStore(dir, schema))
            {
                Assert.Equal(2, store3.BatchCount);
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TableStore_Ownership_LeaveOpen_And_ExplicitDispose()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"glacier_store_own_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var schema = new ArrowSchema([new ArrowField("id", ArrowType.Int32)]);
            using var pool = new Glacier.Storage.BufferPool.BufferPool(4, PageTier.Small);
            using var walStream = new MemoryStream();
            using var wal = new WalTransactionEngine(walStream, leaveOpen: true);

            // Default leaveBufferPoolOpen: true, leaveWalOpen: true must NOT dispose caller resources
            using (var store = new TableStore(dir, schema, pool, wal))
            {
                var tx = store.BeginTransaction();
                int[] ids = [42];
                var col = new ArrowColumn(schema.GetField(0), 1, 0, default, default, MemoryMarshal.AsBytes(ids.AsSpan()).ToArray());
                store.AppendBatch(tx, new ArrowRecordBatch(schema, 1, [col]));
                await store.CommitTransactionAsync(tx);
            }

            // Caller-owned pool and wal must still be usable
            var extraPage = pool.AllocatePage();
            using (var pin = pool.PinPage(extraPage, LockMode.Shared))
            {
                Assert.Equal(4096, pin.Data.Length);
            }
            var extraTx = wal.BeginTransaction();
            await wal.CommitAsync(extraTx);

            // Now pass leaveBufferPoolOpen: false, leaveWalOpen: false
            var pool2 = new Glacier.Storage.BufferPool.BufferPool(2, PageTier.Small);
            var wal2 = new WalTransactionEngine(walStream, leaveOpen: true);
            using (var store2 = new TableStore(dir, schema, pool2, wal2, leaveBufferPoolOpen: false, leaveWalOpen: false))
            {
                Assert.Equal(0, store2.BatchCount);
            }

            Assert.Throws<ObjectDisposedException>(() => pool2.AllocatePage());
            Assert.Throws<ObjectDisposedException>(() => wal2.BeginTransaction());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TableStore_InitFailure_ClosesWalFileHandleAndBufferPool()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"glacier_store_leak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var schema = new ArrowSchema([new ArrowField("id", ArrowType.Int32)]);

        // Seed a valid WAL record whose Arrow IPC payload is deliberately malformed
        string walPath = Path.Combine(dir, "table.wal");
        using (var fs = new FileStream(walPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var wal = new WalTransactionEngine(fs, leaveOpen: false))
        {
            var tx = wal.BeginTransaction();
            wal.LogRecord(tx, new byte[64]); // Invalid Arrow IPC header -> ReplayCommittedWalRecords throws
            await wal.CommitAsync(tx);
        }

        Assert.ThrowsAny<Exception>(() => new TableStore(dir, schema));

        // On Windows, if TableStore leaked walStream, Directory.Delete would throw IOException
        Directory.Delete(dir, recursive: true);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task TableStore_UsesIBufferPool_WithPoolSizeOneAndPageTierSmall_ForLargeBatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"glacier_store_pool1_{Guid.NewGuid():N}");
        try
        {
            var schema = new ArrowSchema([
                new ArrowField("id", ArrowType.Int32),
                new ArrowField("label", ArrowType.Utf8)
            ]);

            const int rows = 4_000; // > 64 KB Arrow IPC payload across > 16 Small (4KB) pages
            var ids = new int[rows];
            var offsets = new int[rows + 1];
            using var strMs = new MemoryStream();
            for (int i = 0; i < rows; i++)
            {
                ids[i] = i;
                offsets[i] = (int)strMs.Position;
                strMs.Write(Encoding.UTF8.GetBytes($"item_{i:D5}"));
            }
            offsets[rows] = (int)strMs.Position;

            var col0 = new ArrowColumn(schema.GetField(0), rows, 0, default, default, MemoryMarshal.AsBytes(ids.AsSpan()).ToArray());
            var col1 = new ArrowColumn(schema.GetField(1), rows, 0, default, MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray(), strMs.ToArray());
            var batch = new ArrowRecordBatch(schema, rows, [col0, col1]);

            using var tinyPool = new Glacier.Storage.BufferPool.BufferPool(poolSize: 1, PageTier.Small);
            using var store = new TableStore(dir, schema, tinyPool);

            var tx = store.BeginTransaction();
            store.AppendBatch(tx, batch);
            await store.CommitTransactionAsync(tx);

            // Verify ReadBatch round-trips from tiny 1-frame pool
            var restored = store.ReadBatch(0);
            Assert.Equal(rows, restored.RowCount);
            Assert.Equal(0, restored.Column(0).GetInt32(0));
            Assert.Equal(rows - 1, restored.Column(0).GetInt32(rows - 1));
            Assert.Equal("item_03999", restored.Column(1).GetString(rows - 1));

            Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadBatch(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadBatch(1));

            // Verify ExportToArrowIpc streams directly from pages
            using var ipcOut = new MemoryStream();
            store.ExportToArrowIpc(ipcOut);
            ipcOut.Position = 0;
            using var reader = new ArrowStreamReader(ipcOut);
            var readBack = reader.ReadNextRecordBatch();
            Assert.NotNull(readBack);
            Assert.Equal(rows, readBack.RowCount);
            Assert.Equal("item_02000", readBack.Column(1).GetString(2000));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
