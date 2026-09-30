namespace Glacier.Storage.Benchmarks;

using System;
using System.IO;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Glacier.Storage.Arrow;

[MemoryDiagnoser]
public class ArrowIpcBenchmarks
{
    private ArrowSchema _schema = null!;
    private ArrowRecordBatch _batch = null!;
    private byte[] _serializedStream = null!;

    [GlobalSetup]
    public void Setup()
    {
        _schema = new ArrowSchema(new[]
        {
            new ArrowField("id", ArrowType.Int32),
            new ArrowField("metric", ArrowType.Double)
        });

        int rowCount = 10000;
        var ids = new int[rowCount];
        var metrics = new double[rowCount];

        for (int i = 0; i < rowCount; i++)
        {
            ids[i] = i;
            metrics[i] = i * 1.25;
        }

        var col0 = new ArrowColumn(_schema.GetField(0), rowCount, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, MemoryMarshal.AsBytes(ids.AsSpan()).ToArray());
        var col1 = new ArrowColumn(_schema.GetField(1), rowCount, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, MemoryMarshal.AsBytes(metrics.AsSpan()).ToArray());

        _batch = new ArrowRecordBatch(_schema, rowCount, new[] { col0, col1 });

        using var ms = new MemoryStream();
        using (var writer = new ArrowStreamWriter(ms, leaveOpen: true))
        {
            writer.WriteSchema(_schema);
            writer.WriteRecordBatch(_batch);
            writer.WriteEndOfStream();
        }

        _serializedStream = ms.ToArray();
    }

    [Benchmark]
    public void SerializeRecordBatch()
    {
        using var ms = new MemoryStream();
        using var writer = new ArrowStreamWriter(ms, leaveOpen: true);
        writer.WriteSchema(_schema);
        writer.WriteRecordBatch(_batch);
        writer.WriteEndOfStream();
    }

    [Benchmark]
    public ArrowRecordBatch? ZeroCopyDeserialize()
    {
        using var ms = new MemoryStream(_serializedStream);
        using var reader = new ArrowStreamReader(ms);
        reader.ReadSchema();
        return reader.ReadNextRecordBatch();
    }
}
