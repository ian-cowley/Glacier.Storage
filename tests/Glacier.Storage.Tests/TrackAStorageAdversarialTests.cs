namespace Glacier.Storage.Tests;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Glacier.Storage;
using Glacier.Formats.Arrow;
using Glacier.Formats.Parquet;
using Xunit;

public class TrackAStorageAdversarialTests
{
    [Fact]
    public void SnappyCodec_Incompressible200KbRandomPayload_ExercisesTag62WithoutTruncation()
    {
        // 200 KB of cryptographically random bytes forces > 64 KB literal runs (Snappy tag 62, 3-byte length)
        byte[] randomData = new byte[204_800];
        RandomNumberGenerator.Fill(randomData);

        byte[] compressed = new byte[SnappyCodec.GetMaxCompressedLength(randomData.Length)];
        int compLen = SnappyCodec.Compress(randomData, compressed);
        Assert.True(compLen > randomData.Length);

        byte[] decompressed = new byte[randomData.Length];
        int decompLen = SnappyCodec.Decompress(compressed.AsSpan(0, compLen), decompressed);

        Assert.Equal(randomData.Length, decompLen);
        Assert.True(randomData.AsSpan().SequenceEqual(decompressed));
    }

    [Fact]
    public void SnappyCodec_Decompress_RejectsHostileMalformedStreams()
    {
        byte[] dst = new byte[4096];

        // 1. Truncated literal over-read: varint = 1000 (0xE8, 0x07), tag 61 (2-byte lenMinus1 = 999 -> 0xE7, 0x03), only 4 payload bytes
        byte[] truncatedLiteral = [0xE8, 0x07, (byte)(61 << 2), 0xE7, 0x03, 1, 2, 3, 4];
        Assert.Throws<InvalidDataException>(() => SnappyCodec.Decompress(truncatedLiteral, dst));

        // 2. Tag 63 literal with uint.MaxValue (0xFFFFFFFF + 1 wrap test)
        byte[] wrapLiteral = [0x10, (byte)(63 << 2), 0xFF, 0xFF, 0xFF, 0xFF, 0xAA];
        Assert.Throws<InvalidDataException>(() => SnappyCodec.Decompress(wrapLiteral, dst));

        // 3. Copy with offset == 0 (1-byte, 2-byte, and 4-byte copy tags) after 1 valid literal byte 'A'
        byte[] zeroOffset1 = [0x08, 0x00, (byte)'A', 0x01, 0x00];
        Assert.Throws<InvalidDataException>(() => SnappyCodec.Decompress(zeroOffset1, dst));

        byte[] zeroOffset2 = [0x08, 0x00, (byte)'A', 0x02, 0x00, 0x00];
        Assert.Throws<InvalidDataException>(() => SnappyCodec.Decompress(zeroOffset2, dst));

        byte[] zeroOffset4 = [0x08, 0x00, (byte)'A', 0x03, 0x00, 0x00, 0x00, 0x00];
        Assert.Throws<InvalidDataException>(() => SnappyCodec.Decompress(zeroOffset4, dst));

        // 4. 4-byte copy with negative int32 bit pattern (0x80000001u)
        byte[] negativeOffset4 = [0x08, 0x00, (byte)'A', 0x03, 0x01, 0x00, 0x00, 0x80];
        Assert.Throws<InvalidDataException>(() => SnappyCodec.Decompress(negativeOffset4, dst));

        // 5. Copy referencing before start of output buffer (1 byte written, offset = 5)
        byte[] outOfBoundsBackref = [0x08, 0x00, (byte)'A', 0x01, 0x05];
        Assert.Throws<InvalidDataException>(() => SnappyCodec.Decompress(outOfBoundsBackref, dst));

        // 6. Stream ends cleanly on tag boundary but decoded fewer bytes than varint header promised
        byte[] shortOutput = [0x10, 0x00, (byte)'A']; // varint promises 16 bytes, only 1 byte emitted
        Assert.Throws<InvalidDataException>(() => SnappyCodec.Decompress(shortOutput, dst));
    }

    [Fact]
    public void SnappyCodec_CompressAndMaxLength_EnforceBoundsAndOverflowChecks()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SnappyCodec.GetMaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SnappyCodec.GetMaxCompressedLength(int.MaxValue));

        Assert.Throws<ArgumentException>(() => SnappyCodec.Compress(ReadOnlySpan<byte>.Empty, Span<byte>.Empty));

        byte[] payload = new byte[1024];
        RandomNumberGenerator.Fill(payload);
        byte[] tooSmall = new byte[64];
        Assert.Throws<ArgumentException>(() => SnappyCodec.Compress(payload, tooSmall));
    }

    private static ArrowRecordBatch CreateTestBatch(ArrowSchema schema, int startId, int rowCount)
    {
        var ids = new int[rowCount];
        var labelsOffsets = new int[rowCount + 1];
        using var strMs = new MemoryStream();

        for (int i = 0; i < rowCount; i++)
        {
            ids[i] = startId + i;
            labelsOffsets[i] = (int)strMs.Position;
            byte[] utf8 = Encoding.UTF8.GetBytes($"row_{startId + i}");
            strMs.Write(utf8);
        }
        labelsOffsets[rowCount] = (int)strMs.Position;

        var col0 = new ArrowColumn(
            schema.GetField(0),
            rowCount,
            0,
            ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<byte>.Empty,
            MemoryMarshal.AsBytes(ids.AsSpan()).ToArray());

        var col1 = new ArrowColumn(
            schema.GetField(1),
            rowCount,
            0,
            ReadOnlyMemory<byte>.Empty,
            MemoryMarshal.AsBytes(labelsOffsets.AsSpan()).ToArray(),
            strMs.ToArray());

        return new ArrowRecordBatch(schema, rowCount, [col0, col1]);
    }

    [Fact]
    public async Task TableStore_MultiBatchParquetExportRollbackAndWalRecovery_PreservesAllCommittedRows()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"glacier_storage_adv_{Guid.NewGuid():N}");
        try
        {
            var schema = new ArrowSchema([
                new ArrowField("id", ArrowType.Int32),
                new ArrowField("label", ArrowType.Utf8)
            ]);

            string parquetPath = Path.Combine(dir, "multi_rg.parquet");
            const int totalExpectedRows = 100 + 150 + 6_000; // 6,000 rows exceeds 64 KB Arrow IPC WAL buffer

            using (var store = new TableStore(dir, schema))
            {
                var tx1 = store.BeginTransaction();
                store.AppendBatch(tx1, CreateTestBatch(schema, 0, 100));
                await store.CommitTransactionAsync(tx1);

                // Aborted transaction MUST be rolled back from memory and ignored on WAL recovery
                var txAborted = store.BeginTransaction();
                store.AppendBatch(txAborted, CreateTestBatch(schema, 999_000, 50));
                store.RollbackTransaction(txAborted);

                var tx2 = store.BeginTransaction();
                store.AppendBatch(tx2, CreateTestBatch(schema, 100, 150));
                store.AppendBatch(tx2, CreateTestBatch(schema, 250, 6_000));
                await store.CommitTransactionAsync(tx2);

                Assert.Equal(3, store.BatchCount);
                store.ExportToParquet(parquetPath);
            }

            // 1. Multi-RowGroup Parquet file must preserve all 6,250 rows across all 3 RowGroups
            ArrowRecordBatch parquetRead = ParquetReader.ReadFile(parquetPath);
            Assert.Equal(totalExpectedRows, parquetRead.RowCount);
            Assert.Equal(0, parquetRead.Column(0).GetInt32(0));
            Assert.Equal("row_0", parquetRead.Column(1).GetString(0));
            Assert.Equal(100, parquetRead.Column(0).GetInt32(100));
            Assert.Equal("row_100", parquetRead.Column(1).GetString(100));
            Assert.Equal(totalExpectedRows - 1, parquetRead.Column(0).GetInt32(totalExpectedRows - 1));
            Assert.Equal($"row_{totalExpectedRows - 1}", parquetRead.Column(1).GetString(totalExpectedRows - 1));

            // 2. Reopening TableStore on the same directory must replay committed Arrow IPC batches from WAL
            using (var reopened = new TableStore(dir, schema))
            {
                Assert.Equal(3, reopened.BatchCount);
                string parquetPath2 = Path.Combine(dir, "recovered.parquet");
                reopened.ExportToParquet(parquetPath2);
                ArrowRecordBatch recoveredParquet = ParquetReader.ReadFile(parquetPath2);
                Assert.Equal(totalExpectedRows, recoveredParquet.RowCount);
                Assert.Equal(totalExpectedRows - 1, recoveredParquet.Column(0).GetInt32(totalExpectedRows - 1));
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
