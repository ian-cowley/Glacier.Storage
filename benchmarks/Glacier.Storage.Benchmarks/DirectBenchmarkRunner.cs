namespace Glacier.Storage.Benchmarks;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Glacier.DocTree.GDoc;
using Glacier.Formats.Arrow;
using Glacier.Formats.Parquet;
using Glacier.Storage.BufferPool;
using Glacier.Storage.Encoding;
using Glacier.Storage.Wal;

public static class DirectBenchmarkRunner
{
    public static void RunAll()
    {
        Console.WriteLine("\n[1] Running Vectorized Bit-Unpacker Benchmark (10,000,000 values)...");
        RunBitUnpackerBenchmark();

        Console.WriteLine("\n[2] Running SIMD Snappy Compression/Decompression Benchmark (100 MB)...");
        RunSnappyBenchmark();

        Console.WriteLine("\n[3] Running Arrow IPC Streaming (Serialize & Zero-Copy Slicing)...");
        RunArrowIpcBenchmark();

        Console.WriteLine("\n[4] Running Clock-Pro Multi-Tier Buffer Pool Benchmark...");
        RunBufferPoolBenchmark();

        Console.WriteLine("\n[5] Running ACID Write-Ahead Log (WAL) Group-Commit Benchmark...");
        RunWalBenchmark();

        Console.WriteLine("\n[6] Running .gdoc Contiguous Memory-Mapped Traversal & SIMD Cosine Search...");
        RunGDocBenchmark();

        Console.WriteLine("\n[7] Running Lossless ALP Float/Double Compression Benchmark (1,000,000 values)...");
        RunAlpBenchmark();

        Console.WriteLine("\n================================================================================");
        Console.WriteLine("All benchmarks completed successfully.");
        Console.WriteLine("================================================================================");
    }

    private static void RunBitUnpackerBenchmark()
    {
        const int count = 10_000_000;
        var destination = new uint[count];
        var packed4Bit = new byte[count / 2];
        for (int i = 0; i < packed4Bit.Length; i++) packed4Bit[i] = 0x5A;

        // Warmup
        VectorizedBitUnpacker.Unpack(packed4Bit, 4, destination);

        var sw = Stopwatch.StartNew();
        int iterations = 10;
        for (int iter = 0; iter < iterations; iter++)
        {
            VectorizedBitUnpacker.Unpack(packed4Bit, 4, destination);
        }
        sw.Stop();

        double totalValues = (double)count * iterations;
        double throughput = (totalValues / 1_000_000.0) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  Unpacked {totalValues:N0} 4-bit values in {sw.ElapsedMilliseconds} ms -> {throughput:N1} M values/sec");
    }

    private static void RunSnappyBenchmark()
    {
        const int size = 10 * 1024 * 1024; // 10 MB
        var source = new byte[size];
        for (int i = 0; i < size; i++) source[i] = (byte)(i % 23);

        var comp = new byte[SnappyCodec.GetMaxCompressedLength(size)];
        int compLen = SnappyCodec.Compress(source, comp);

        var decomp = new byte[size];

        // Compress throughput
        int iters = 5;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            SnappyCodec.Compress(source, comp);
        }
        sw.Stop();
        double compThroughput = ((double)size * iters / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  Snappy Compression: {compThroughput:N1} MB/sec (ratio: {(double)size / compLen:N2}x)");

        // Decompress throughput
        sw.Restart();
        for (int i = 0; i < iters; i++)
        {
            SnappyCodec.Decompress(comp.AsSpan(0, compLen), decomp);
        }
        sw.Stop();
        double decompThroughput = ((double)size * iters / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  Snappy Decompression: {decompThroughput:N1} MB/sec");
    }

    private static void RunArrowIpcBenchmark()
    {
        int rows = 100_000;
        var schema = new ArrowSchema(new[]
        {
            new ArrowField("id", ArrowType.Int32),
            new ArrowField("val", ArrowType.Double)
        });

        var ids = new int[rows];
        var vals = new double[rows];
        for (int i = 0; i < rows; i++) { ids[i] = i; vals[i] = i * 2.5; }

        var col0 = new ArrowColumn(schema.GetField(0), rows, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, MemoryMarshal.AsBytes(ids.AsSpan()).ToArray());
        var col1 = new ArrowColumn(schema.GetField(1), rows, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, MemoryMarshal.AsBytes(vals.AsSpan()).ToArray());
        var batch = new ArrowRecordBatch(schema, rows, new[] { col0, col1 });

        using var ms = new MemoryStream();
        var sw = Stopwatch.StartNew();
        using (var writer = new ArrowStreamWriter(ms, leaveOpen: true))
        {
            writer.WriteSchema(schema);
            for (int i = 0; i < 5; i++)
            {
                writer.WriteRecordBatch(batch);
            }
            writer.WriteEndOfStream();
        }
        sw.Stop();
        Console.WriteLine($"  Arrow IPC Write (500K rows): {sw.ElapsedMilliseconds} ms");

        ms.Seek(0, SeekOrigin.Begin);
        sw.Restart();
        using (var reader = new ArrowStreamReader(ms))
        {
            reader.ReadSchema();
            int batchesRead = 0;
            while (reader.ReadNextRecordBatch() != null)
            {
                batchesRead++;
            }
        }
        sw.Stop();
        Console.WriteLine($"  Arrow IPC Zero-Copy Read (500K rows): {sw.ElapsedMilliseconds} ms");
    }

    private static void RunBufferPoolBenchmark()
    {
        using var pool = new Glacier.Storage.BufferPool.BufferPool(256, PageTier.Medium);
        var pages = new PageId[256];
        for (int i = 0; i < pages.Length; i++) pages[i] = pool.AllocatePage();

        const int pinOps = 1_000_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < pinOps; i++)
        {
            var pId = pages[i & 255];
            using var pin = pool.PinPage(pId, LockMode.Shared);
        }
        sw.Stop();

        double opsSec = (pinOps / 1_000_000.0) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  BufferPool Pin/Unpin: {sw.ElapsedMilliseconds} ms for {pinOps:N0} pins -> {opsSec:N2} M ops/sec");
    }

    private static void RunWalBenchmark()
    {
        using var ms = new MemoryStream();
        using var wal = new WalTransactionEngine(ms, bufferCapacity: 128 * 1024, groupCommitIntervalMs: 2, leaveOpen: true);

        byte[] payload = "TransactionRecordPayloadGlacierWal"u8.ToArray();
        const int txCount = 50_000;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < txCount; i++)
        {
            var tx = wal.BeginTransaction();
            wal.LogRecord(tx, payload);
            if ((i & 63) == 0)
            {
                wal.CommitAsync(tx).GetAwaiter().GetResult();
            }
        }
        wal.Flush();
        sw.Stop();

        double txSec = (txCount / 1000.0) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  ACID WAL Appends: {sw.ElapsedMilliseconds} ms for {txCount:N0} tx -> {txSec:N1} K tx/sec");
    }

    private static void RunGDocBenchmark()
    {
        var writer = new GDocWriter(vectorDim: 768);
        var qVec = new float[768];
        qVec[0] = 1.0f;

        for (int i = 0; i < 1000; i++)
        {
            var vec = new float[768];
            vec[i % 768] = 0.9f;
            uint vIdx = writer.AddVector(vec);
            writer.AddNode(GDocNodeType.Paragraph, $"Section content number {i}", vectorIndex: vIdx);
        }

        byte[] bytes = writer.ToByteArray();
        using var reader = GDocReader.FromMemory(bytes);

        var sw = Stopwatch.StartNew();
        const int searchIters = 200;
        for (int i = 0; i < searchIters; i++)
        {
            reader.FindBestMatch(qVec);
        }
        sw.Stop();

        double searchesSec = searchIters / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  SIMD 768-D Vector Cosine Search (1,000 nodes): {sw.ElapsedMilliseconds} ms for {searchIters} queries -> {searchesSec:N0} queries/sec");
    }

    private static void RunAlpBenchmark()
    {
        const int blockCount = 16;
        const int blockSize = 62_500;
        const int totalValues = blockCount * blockSize;

        var floatBlock = new float[blockSize];
        var doubleBlock = new double[blockSize];
        for (int i = 0; i < blockSize; i++)
        {
            floatBlock[i] = 100.0f + (i % 1000) * 0.25f;
            doubleBlock[i] = 1000.0 + (i % 1000) * 0.125;
        }

        var floatDst = new byte[AlpCompressor.GetMaxCompressedLength(blockSize, sizeof(float))];
        var floatOut = new float[blockSize];
        var doubleDst = new byte[AlpCompressor.GetMaxCompressedLength(blockSize, sizeof(double))];
        var doubleOut = new double[blockSize];

        int fLen = AlpCompressor.CompressFloat(floatBlock, floatDst);
        AlpCompressor.DecompressFloat(floatDst.AsSpan(0, fLen), floatOut);
        int dLen = AlpCompressor.CompressDouble(doubleBlock, doubleDst);
        AlpCompressor.DecompressDouble(doubleDst.AsSpan(0, dLen), doubleOut);

        var sw = Stopwatch.StartNew();
        for (int b = 0; b < blockCount; b++) fLen = AlpCompressor.CompressFloat(floatBlock, floatDst);
        sw.Stop();
        double fCompThroughput = (totalValues / 1_000_000.0) / sw.Elapsed.TotalSeconds;

        sw.Restart();
        for (int b = 0; b < blockCount; b++) AlpCompressor.DecompressFloat(floatDst.AsSpan(0, fLen), floatOut);
        sw.Stop();
        double fDecompThroughput = (totalValues / 1_000_000.0) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  ALP Float32 (1M vals): Compress {fCompThroughput:N1} M vals/s (ratio {(double)(blockSize * sizeof(float)) / fLen:N2}x) | Decompress {fDecompThroughput:N1} M vals/s");

        sw.Restart();
        for (int b = 0; b < blockCount; b++) dLen = AlpCompressor.CompressDouble(doubleBlock, doubleDst);
        sw.Stop();
        double dCompThroughput = (totalValues / 1_000_000.0) / sw.Elapsed.TotalSeconds;

        sw.Restart();
        for (int b = 0; b < blockCount; b++) AlpCompressor.DecompressDouble(doubleDst.AsSpan(0, dLen), doubleOut);
        sw.Stop();
        double dDecompThroughput = (totalValues / 1_000_000.0) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  ALP Float64 (1M vals): Compress {dCompThroughput:N1} M vals/s (ratio {(double)(blockSize * sizeof(double)) / dLen:N2}x) | Decompress {dDecompThroughput:N1} M vals/s");
    }
}
