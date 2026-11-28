using System;
using System.Buffers.Binary;
using System.IO;
using Glacier.Storage.Encoding;
using Xunit;

namespace Glacier.Storage.Tests;

public class StorageAlpAdversarialTests
{
    [Fact]
    public void StorageAlp_SpecialIeee754Values_BitExactRoundtrip()
    {
        float[] specialFloats =
        [
            0.0f,
            -0.0f,
            float.Epsilon,
            -float.Epsilon,
            1e-40f,
            -1e-40f,
            float.NaN,
            BitConverter.UInt32BitsToSingle(0x7F800001), // signaling NaN
            BitConverter.UInt32BitsToSingle(0x7FC01234), // quiet NaN with payload
            float.PositiveInfinity,
            float.NegativeInfinity,
            float.MinValue,
            float.MaxValue,
            12.345f
        ];

        byte[] destF = new byte[AlpCompressor.GetMaxCompressedLength(specialFloats.Length, sizeof(float))];
        int writtenF = AlpCompressor.CompressFloat(specialFloats, destF, out int exF);
        Assert.True(writtenF > 0);
        Assert.True(exF > 0);

        float[] restoredF = new float[specialFloats.Length];
        AlpCompressor.DecompressFloat(destF.AsSpan(0, writtenF), restoredF);

        for (int i = 0; i < specialFloats.Length; i++)
        {
            Assert.Equal(
                BitConverter.SingleToUInt32Bits(specialFloats[i]),
                BitConverter.SingleToUInt32Bits(restoredF[i]));
        }

        double[] specialDoubles =
        [
            0.0,
            -0.0,
            double.Epsilon,
            -double.Epsilon,
            1e-300,
            -1e-300,
            double.NaN,
            BitConverter.UInt64BitsToDouble(0x7FF0000000000001UL), // signaling NaN
            BitConverter.UInt64BitsToDouble(0x7FF8000000004242UL), // quiet NaN payload
            double.PositiveInfinity,
            double.NegativeInfinity,
            double.MinValue,
            double.MaxValue,
            987.654321
        ];

        byte[] destD = new byte[AlpCompressor.GetMaxCompressedLength(specialDoubles.Length, sizeof(double))];
        int writtenD = AlpCompressor.CompressDouble(specialDoubles, destD, out int exD);
        Assert.True(writtenD > 0);
        Assert.True(exD > 0);

        double[] restoredD = new double[specialDoubles.Length];
        AlpCompressor.DecompressDouble(destD.AsSpan(0, writtenD), restoredD);

        for (int i = 0; i < specialDoubles.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(specialDoubles[i]),
                BitConverter.DoubleToUInt64Bits(restoredD[i]));
        }
    }

    [Fact]
    public void StorageAlp_AlpRdDouble_HighPrecision_BitExactRoundtrip()
    {
        double[] highPrecisionDoubles = new double[128];
        for (int i = 0; i < highPrecisionDoubles.Length; i++)
        {
            highPrecisionDoubles[i] = 250000.0 + (i * 0.000123456);
        }

        byte[] dest = new byte[AlpCompressor.GetMaxCompressedLength(highPrecisionDoubles.Length, sizeof(double))];
        int written = AlpCompressor.CompressDouble(highPrecisionDoubles, dest, out _);

        double[] restored = new double[highPrecisionDoubles.Length];
        AlpCompressor.DecompressDouble(dest.AsSpan(0, written), restored);

        for (int i = 0; i < highPrecisionDoubles.Length; i++)
        {
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(highPrecisionDoubles[i]),
                BitConverter.DoubleToUInt64Bits(restored[i]));
        }
    }

    [Fact]
    public void StorageAlp_ZeroAllocation_SteadyState()
    {
        float[] floats = new float[128];
        for (int i = 0; i < floats.Length; i++) floats[i] = (float)(10.0 + i * 0.1);

        byte[] dest = new byte[AlpCompressor.GetMaxCompressedLength(floats.Length, sizeof(float))];
        float[] restored = new float[floats.Length];

        // Warm up
        for (int i = 0; i < 5; i++)
        {
            int w = AlpCompressor.CompressFloat(floats, dest);
            AlpCompressor.DecompressFloat(dest.AsSpan(0, w), restored);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int written = AlpCompressor.CompressFloat(floats, dest);
        AlpCompressor.DecompressFloat(dest.AsSpan(0, written), restored);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void StorageAlp_CorruptBuffer_ThrowsInvalidDataException()
    {
        byte[] badMagic = new byte[AlpCompressor.HeaderSize];
        Array.Fill(badMagic, (byte)0xEE);
        float[] dest = new float[10];

        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(badMagic, dest));
    }

    [Fact]
    public void StorageAlp_InvalidHeaderBounds_ThrowsInvalidDataException()
    {
        // 1. ReadHeader < 48 bytes
        byte[] shortBuf = new byte[AlpCompressor.HeaderSize - 1];
        Assert.Throws<InvalidDataException>(() => AlpCompressor.ReadHeader(
            shortBuf, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _));

        // 2. GetMaxCompressedLength negative / overflow
        Assert.Throws<ArgumentOutOfRangeException>(() => AlpCompressor.GetMaxCompressedLength(-1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => AlpCompressor.GetMaxCompressedLength(10, 0));
        Assert.Throws<OverflowException>(() => AlpCompressor.GetMaxCompressedLength(int.MaxValue, 8));

        // 3. Codec negative originalCount
        var codec = new AlpCompressionCodec();
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.Decompress(ReadOnlySpan<byte>.Empty, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.DecompressDouble(ReadOnlySpan<byte>.Empty, -1));

        // Helper to build synthetic ALP header + payload
        static byte[] BuildRawAlp(AlpCompressionMode mode, byte exp, byte bw, byte expR, byte bwR, ushort count, ushort exCount, int payloadBytes)
        {
            byte[] buf = new byte[AlpCompressor.HeaderSize + payloadBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x00, 4), AlpCompressor.Magic);
            buf[0x04] = (byte)mode;
            buf[0x05] = exp;
            buf[0x06] = bw;
            buf[0x07] = expR;
            buf[0x08] = bwR;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x0A, 2), count);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x0C, 2), exCount);
            return buf;
        }

        float[] fDest = new float[8];
        double[] dDest = new double[8];

        // 4. AlpFloat invalid exp (> 18), bitWidth (> 64), exCount (> count), truncated, and idx >= count
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(BuildRawAlp(AlpCompressionMode.AlpFloat, 19, 4, 0, 0, 4, 0, 16), fDest));
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(BuildRawAlp(AlpCompressionMode.AlpFloat, 1, 65, 0, 0, 4, 0, 64), fDest));
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(BuildRawAlp(AlpCompressionMode.AlpFloat, 1, 4, 0, 0, 4, 5, 64), fDest));
        // Truncated stream with large count & tiny destination must throw InvalidDataException (not ArgumentException)
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(BuildRawAlp(AlpCompressionMode.AlpFloat, 1, 8, 0, 0, 500, 0, 4), fDest));
        // Out-of-bounds exception index (idx = 4 for count = 4) must throw InvalidDataException without mutating destination
        byte[] badExFloat = BuildRawAlp(AlpCompressionMode.AlpFloat, 1, 0, 0, 0, 4, 1, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(badExFloat.AsSpan(AlpCompressor.HeaderSize, 2), 4);
        Array.Fill(fDest, 123.5f);
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressFloat(badExFloat, fDest));
        Assert.Equal(123.5f, fDest[0]);

        // 5. AlpDouble invalid bounds
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(BuildRawAlp(AlpCompressionMode.AlpDouble, 19, 4, 0, 0, 4, 0, 16), dDest));
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(BuildRawAlp(AlpCompressionMode.AlpDouble, 1, 65, 0, 0, 4, 0, 64), dDest));
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(BuildRawAlp(AlpCompressionMode.AlpDouble, 1, 4, 0, 0, 4, 5, 64), dDest));
        byte[] badExDouble = BuildRawAlp(AlpCompressionMode.AlpDouble, 1, 0, 0, 0, 4, 1, 10);
        BinaryPrimitives.WriteUInt16LittleEndian(badExDouble.AsSpan(AlpCompressor.HeaderSize, 2), 99);
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(badExDouble, dDest));

        // 6. AlpRdDouble invalid bounds
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(BuildRawAlp(AlpCompressionMode.AlpRdDouble, 0, 4, 19, 4, 4, 0, 16), dDest));
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(BuildRawAlp(AlpCompressionMode.AlpRdDouble, 0, 65, 2, 4, 4, 0, 64), dDest));
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(BuildRawAlp(AlpCompressionMode.AlpRdDouble, 0, 4, 2, 65, 4, 0, 64), dDest));
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(BuildRawAlp(AlpCompressionMode.AlpRdDouble, 0, 4, 2, 4, 4, 5, 64), dDest));
        byte[] badExRd = BuildRawAlp(AlpCompressionMode.AlpRdDouble, 0, 0, 2, 0, 4, 1, 10);
        BinaryPrimitives.WriteUInt16LittleEndian(badExRd.AsSpan(AlpCompressor.HeaderSize, 2), 4);
        Assert.Throws<InvalidDataException>(() => AlpCompressor.DecompressDouble(badExRd, dDest));
    }
}
