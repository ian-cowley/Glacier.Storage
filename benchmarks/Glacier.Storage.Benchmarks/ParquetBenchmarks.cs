namespace Glacier.Storage.Benchmarks;

using System;
using BenchmarkDotNet.Attributes;
using Glacier.Storage.Parquet;

[MemoryDiagnoser]
public class ParquetBitUnpackBenchmark
{
    private const int Count = 65536;
    private byte[] _packed4Bit = null!;
    private byte[] _packed8Bit = null!;
    private uint[] _destination = null!;

    [GlobalSetup]
    public void Setup()
    {
        _destination = new uint[Count];
        _packed4Bit = new byte[Count / 2];
        _packed8Bit = new byte[Count];

        for (int i = 0; i < Count / 2; i++)
        {
            _packed4Bit[i] = (byte)((i & 0x0F) | ((i & 0x0F) << 4));
        }

        for (int i = 0; i < Count; i++)
        {
            _packed8Bit[i] = (byte)(i & 0xFF);
        }
    }

    [Benchmark(Baseline = true)]
    public void Unpack8Bit()
    {
        VectorizedBitUnpacker.Unpack(_packed8Bit, 8, _destination);
    }

    [Benchmark]
    public void Unpack4Bit()
    {
        VectorizedBitUnpacker.Unpack(_packed4Bit, 4, _destination);
    }
}

[MemoryDiagnoser]
public class SnappyCompressionBenchmark
{
    private byte[] _sourceData = null!;
    private byte[] _compressed = null!;
    private byte[] _decompressed = null!;
    private int _compressedLength;

    [GlobalSetup]
    public void Setup()
    {
        _sourceData = new byte[65536];
        for (int i = 0; i < _sourceData.Length; i++)
        {
            _sourceData[i] = (byte)(i % 19);
        }

        _compressed = new byte[SnappyCodec.GetMaxCompressedLength(_sourceData.Length)];
        _compressedLength = SnappyCodec.Compress(_sourceData, _compressed);
        _decompressed = new byte[_sourceData.Length];
    }

    [Benchmark]
    public int Compress()
    {
        return SnappyCodec.Compress(_sourceData, _compressed);
    }

    [Benchmark]
    public int Decompress()
    {
        return SnappyCodec.Decompress(_compressed.AsSpan(0, _compressedLength), _decompressed);
    }
}
