using System;
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
}
