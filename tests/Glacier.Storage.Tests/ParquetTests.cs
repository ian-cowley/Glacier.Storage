namespace Glacier.Storage.Tests;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Glacier.Storage.Arrow;
using Glacier.Storage.Parquet;
using Xunit;

public class ParquetTests
{
    [Fact]
    public void ThriftCompactProtocol_PrimitiveRoundtrip()
    {
        using var ms = new MemoryStream();
        var writer = new ThriftCompactWriter(ms);

        writer.WriteStructBegin();
        writer.WriteFieldBegin(1, ThriftType.I32);
        writer.WriteI32(12345);
        writer.WriteFieldBegin(2, ThriftType.I64);
        writer.WriteI64(9876543210L);
        writer.WriteFieldBegin(3, ThriftType.Binary);
        writer.WriteString("Glacier.Parquet.Thrift");
        writer.WriteStructEnd();

        ms.Seek(0, SeekOrigin.Begin);
        var reader = new ThriftCompactReader(ms);
        reader.ReadStructBegin();

        Assert.True(reader.ReadFieldBegin(out short f1, out var t1));
        Assert.Equal(1, f1);
        Assert.Equal(12345, reader.ReadI32());

        Assert.True(reader.ReadFieldBegin(out short f2, out var t2));
        Assert.Equal(2, f2);
        Assert.Equal(9876543210L, reader.ReadI64());

        Assert.True(reader.ReadFieldBegin(out short f3, out var t3));
        Assert.Equal(3, f3);
        Assert.Equal("Glacier.Parquet.Thrift", reader.ReadString());

        Assert.False(reader.ReadFieldBegin(out _, out _));
    }

    [Fact]
    public void SnappyCodec_Roundtrip_ArbitraryData()
    {
        // Test with repetitive data (high compression)
        byte[] original = new byte[65536];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i % 17);
        }

        byte[] compBuf = new byte[SnappyCodec.GetMaxCompressedLength(original.Length)];
        int compLen = SnappyCodec.Compress(original, compBuf);
        Assert.True(compLen < original.Length / 2, "Repetitive data should compress significantly.");

        byte[] decompBuf = new byte[original.Length];
        int decompLen = SnappyCodec.Decompress(compBuf.AsSpan(0, compLen), decompBuf);

        Assert.Equal(original.Length, decompLen);
        Assert.True(original.AsSpan().SequenceEqual(decompBuf));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void VectorizedBitUnpacker_VariousBitWidths_MatchesExpected(int bitWidth)
    {
        int count = 64;
        uint maxVal = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        var original = new uint[count];
        for (int i = 0; i < count; i++)
        {
            original[i] = (uint)(i * 7) & maxVal;
        }

        // Pack values into bytes
        int totalBits = count * bitWidth;
        int totalBytes = (totalBits + 7) / 8;
        byte[] packed = new byte[totalBytes + 8]; // extra padding for unaligned reads

        int bitOffset = 0;
        for (int i = 0; i < count; i++)
        {
            uint val = original[i];
            for (int b = 0; b < bitWidth; b++)
            {
                if (((val >> b) & 1) != 0)
                {
                    int byteIdx = (bitOffset + b) >> 3;
                    int bitIdx = (bitOffset + b) & 7;
                    packed[byteIdx] |= (byte)(1 << bitIdx);
                }
            }
            bitOffset += bitWidth;
        }

        var unpacked = new uint[count];
        VectorizedBitUnpacker.Unpack(packed, bitWidth, unpacked);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(original[i], unpacked[i]);
        }
    }

    [Fact]
    public void ParquetRleCodec_EncodeDecode_Roundtrip()
    {
        int count = 128;
        var values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = (uint)(i % 15);
        }

        byte[] dest = new byte[1024];
        int encodedBytes = ParquetRleCodec.Encode(values, 4, dest);
        Assert.True(encodedBytes > 0);

        var decoded = new uint[count];
        int decodedCount = ParquetRleCodec.Decode(dest.AsSpan(0, encodedBytes), 4, decoded);

        Assert.Equal(count, decodedCount);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(values[i], decoded[i]);
        }
    }

    [Fact]
    public void ParquetWriterReader_FullRoundtrip_MultipleColumns()
    {
        var schema = new ArrowSchema(new[]
        {
            new ArrowField("id", ArrowType.Int32),
            new ArrowField("weight", ArrowType.Double),
            new ArrowField("city", ArrowType.Utf8)
        });

        int rowCount = 250;
        var ids = new int[rowCount];
        var weights = new double[rowCount];
        var offsets = new int[rowCount + 1];
        using var strMs = new MemoryStream();

        for (int i = 0; i < rowCount; i++)
        {
            ids[i] = i + 100;
            weights[i] = 70.5 + (i * 0.1);
            offsets[i] = (int)strMs.Position;
            byte[] cityBytes = Encoding.UTF8.GetBytes(i % 2 == 0 ? "Seattle" : "Zurich");
            strMs.Write(cityBytes);
        }
        offsets[rowCount] = (int)strMs.Position;

        byte[] idBytes = MemoryMarshal.AsBytes(ids.AsSpan()).ToArray();
        byte[] weightBytes = MemoryMarshal.AsBytes(weights.AsSpan()).ToArray();
        byte[] offsetsBytes = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
        byte[] cityData = strMs.ToArray();

        var col0 = new ArrowColumn(schema.GetField(0), rowCount, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, idBytes);
        var col1 = new ArrowColumn(schema.GetField(1), rowCount, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, weightBytes);
        var col2 = new ArrowColumn(schema.GetField(2), rowCount, 0, ReadOnlyMemory<byte>.Empty, offsetsBytes, cityData);

        var batch = new ArrowRecordBatch(schema, rowCount, new[] { col0, col1, col2 });

        byte[] parquetBytes = ParquetWriter.WriteToBytes(batch, CompressionCodec.Snappy);
        Assert.NotNull(parquetBytes);
        Assert.True(parquetBytes.Length > 12);

        // Read back
        var readBatch = ParquetReader.ReadBytes(parquetBytes);
        Assert.NotNull(readBatch);
        Assert.Equal(rowCount, readBatch.RowCount);
        Assert.Equal(3, readBatch.Columns.Count);

        for (int i = 0; i < rowCount; i++)
        {
            Assert.Equal(ids[i], readBatch.Column(0).GetInt32(i));
            Assert.Equal(weights[i], readBatch.Column(1).GetDouble(i), 4);
            Assert.Equal(i % 2 == 0 ? "Seattle" : "Zurich", readBatch.Column(2).GetString(i));
        }
    }
}
