namespace Glacier.Storage.Tests;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Glacier.Storage;
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
}
