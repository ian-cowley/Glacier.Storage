namespace Glacier.Storage.Benchmarks;

using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using Glacier.Storage;
using Glacier.Storage.BufferPool;
using Glacier.Storage.Doc;
using Glacier.Storage.Wal;

[MemoryDiagnoser]
public class BufferPoolBenchmark
{
    private Glacier.Storage.BufferPool.BufferPool _pool = null!;
    private PageId[] _pages = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pool = new Glacier.Storage.BufferPool.BufferPool(128, PageTier.Small);
        _pages = new PageId[128];
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i] = _pool.AllocatePage();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Dispose();
    }

    [Benchmark]
    public void PinUnpinCycle()
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            using var pin = _pool.PinPage(_pages[i], LockMode.Shared);
        }
    }
}

[MemoryDiagnoser]
public class WalBenchmark
{
    private MemoryStream _ms = null!;
    private WalTransactionEngine _engine = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ms = new MemoryStream();
        _engine = new WalTransactionEngine(_ms, bufferCapacity: 65536, groupCommitIntervalMs: 1, leaveOpen: true);
        _payload = "ColumnarStorageWalRecordUpdatePayload"u8.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _engine.Dispose();
        _ms.Dispose();
    }

    [Benchmark]
    public void LogRecordAndCommit()
    {
        var tx = _engine.BeginTransaction();
        _engine.LogRecord(tx, _payload);
        _engine.CommitAsync(tx).GetAwaiter().GetResult();
    }
}

[MemoryDiagnoser]
public class GDocBenchmark
{
    private byte[] _gdocBytes = null!;
    private GDocReader _reader = null!;
    private float[] _queryVec = null!;

    [GlobalSetup]
    public void Setup()
    {
        var writer = new GDocWriter(vectorDim: 768);
        _queryVec = new float[768];
        _queryVec[0] = 1.0f;

        for (int i = 0; i < 500; i++)
        {
            var vec = new float[768];
            vec[i % 768] = 0.8f;
            uint vIdx = writer.AddVector(vec);
            writer.AddNode(GDocNodeType.Paragraph, $"Document AST node {i} text content for benchmarking", vectorIndex: vIdx);
        }

        _gdocBytes = writer.ToByteArray();
        _reader = GDocReader.FromMemory(_gdocBytes);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
    }

    [Benchmark]
    public (uint NodeId, float Score) SimdVectorCosineSearch()
    {
        return _reader.FindBestMatch(_queryVec);
    }
}
