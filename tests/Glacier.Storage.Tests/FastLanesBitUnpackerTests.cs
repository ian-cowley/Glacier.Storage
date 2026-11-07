namespace Glacier.Storage.Tests;

using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Glacier.Storage.Encoding;
using Xunit;

public class FastLanesBitUnpackerTests
{
    private static void ReferenceOracleUnpack16(ReadOnlySpan<byte> packed, Span<uint> unpacked, int bitWidth)
    {
        uint mask = bitWidth == 32 ? uint.MaxValue : ((1u << bitWidth) - 1u);
        int bitOffset = 0;

        for (int lane = 0; lane < 16 && lane < unpacked.Length; lane++)
        {
            int byteStart = bitOffset >> 3;
            int bitInByte = bitOffset & 7;

            ulong accumulator = 0;
            int bytesLeft = packed.Length - byteStart;
            for (int b = 0; b < 8 && b < bytesLeft; b++)
            {
                accumulator |= ((ulong)packed[byteStart + b]) << (b * 8);
            }

            unpacked[lane] = (uint)((accumulator >> bitInByte) & mask);
            bitOffset += bitWidth;
        }
    }

    private static void ReferenceOracleUnpack8_64(ReadOnlySpan<byte> packed, Span<ulong> unpacked, int bitWidth)
    {
        ulong mask = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1UL);
        int bitOffset = 0;

        for (int lane = 0; lane < 8 && lane < unpacked.Length; lane++)
        {
            int byteStart = bitOffset >> 3;
            int bitInByte = bitOffset & 7;

            ulong accumulator = 0;
            int bytesLeft = packed.Length - byteStart;
            for (int b = 0; b < 8 && b < bytesLeft; b++)
            {
                accumulator |= ((ulong)packed[byteStart + b]) << (b * 8);
            }

            ulong val = (accumulator >> bitInByte) & mask;
            if (bitInByte + bitWidth > 64 && (byteStart + 8) < packed.Length)
            {
                ulong high = (ulong)packed[byteStart + 8];
                val |= (high << (64 - bitInByte)) & mask;
            }

            unpacked[lane] = val;
            bitOffset += bitWidth;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(32)]
    public void Unpack16_MatchesOracle_AllBitWidths(int bitWidth)
    {
        int requiredBytes = (16 * bitWidth + 7) / 8;
        byte[] packed = new byte[requiredBytes + 16]; // with padding
        var rng = new Random(42 + bitWidth);
        rng.NextBytes(packed);

        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        FastLanesBitUnpacker.Unpack16(packed.AsSpan(0, requiredBytes), actual, bitWidth);
        ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, bitWidth);

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
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
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(56)]
    [InlineData(63)]
    [InlineData(64)]
    public void Unpack8Lanes64_MatchesOracle_AllBitWidths(int bitWidth)
    {
        int requiredBytes = (8 * bitWidth + 7) / 8;
        byte[] packed = new byte[requiredBytes + 16];
        var rng = new Random(100 + bitWidth);
        rng.NextBytes(packed);

        Span<ulong> actual = stackalloc ulong[8];
        Span<ulong> expected = stackalloc ulong[8];

        FastLanesBitUnpacker.Unpack8Lanes64(packed.AsSpan(0, requiredBytes), actual, bitWidth);
        ReferenceOracleUnpack8_64(packed.AsSpan(0, requiredBytes), expected, bitWidth);

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void Unpack16_AllZeros_ProducesAllZeros()
    {
        byte[] packed = new byte[64];
        Span<uint> unpacked = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            FastLanesBitUnpacker.Unpack16(packed, unpacked, w);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(0u, unpacked[i]);
            }
        }
    }

    [Fact]
    public void Unpack16_AllOnes_ProducesMaxValues()
    {
        byte[] packed = new byte[64];
        Array.Fill(packed, (byte)0xFF);
        Span<uint> unpacked = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            FastLanesBitUnpacker.Unpack16(packed, unpacked, w);
            uint expectedVal = w == 32 ? uint.MaxValue : ((1u << w) - 1u);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(expectedVal, unpacked[i]);
            }
        }
    }

    [Fact]
    public void Unpack16_CheckerboardPattern_MatchesExpected()
    {
        // 16 4-bit nibbles: [0, 1, 2, ..., 15]
        byte[] packed = [0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC, 0xFE];
        Span<uint> unpacked = stackalloc uint[16];

        FastLanesBitUnpacker.Unpack16(packed, unpacked, bitWidth: 4);

        for (uint i = 0; i < 16; i++)
        {
            Assert.Equal(i, unpacked[(int)i]);
        }
    }

    [Fact]
    public void Unpack16_UnalignedSliceOffsets_Succeeds()
    {
        byte[] largeBuffer = new byte[128];
        var rng = new Random(12345);
        rng.NextBytes(largeBuffer);

        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        // Test offsets 0, 1, 2, 3, 5, 7
        int[] offsets = [0, 1, 2, 3, 5, 7];
        foreach (int off in offsets)
        {
            FastLanesBitUnpacker.Unpack16(largeBuffer.AsSpan(off), actual, bitWidth: 10);
            ReferenceOracleUnpack16(largeBuffer.AsSpan(off), expected, bitWidth: 10);

            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }
    }

    [Fact]
    public void Unpack16_BitWidthExceptions_ThrowArgumentOutOfRange()
    {
        byte[] packed = new byte[64];
        uint[] unpacked = new uint[16];

        Assert.Throws<ArgumentOutOfRangeException>(() => FastLanesBitUnpacker.Unpack16(packed, unpacked, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FastLanesBitUnpacker.Unpack16(packed, unpacked, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FastLanesBitUnpacker.Unpack16(packed, unpacked, 33));
    }

    [Fact]
    public void Unpack16_BufferTooShort_ThrowsArgumentException()
    {
        byte[] packed = new byte[2]; // Need 24 bytes for W=12
        uint[] unpacked = new uint[16];

        Assert.Throws<ArgumentException>(() => FastLanesBitUnpacker.Unpack16(packed, unpacked, 12));
    }

    [Fact]
    public void Unpack1024_BlockUnpack_MatchesSequentialUnpack()
    {
        int bitWidth = 8;
        int blockBytes = 1024;
        byte[] packed = new byte[blockBytes];
        for (int i = 0; i < blockBytes; i++) packed[i] = (byte)(i & 0xFF);

        Span<uint> destination = stackalloc uint[1024];
        FastLanesBitUnpacker.Unpack1024(packed, destination, bitWidth);

        for (int i = 0; i < 1024; i++)
        {
            Assert.Equal((uint)(i & 0xFF), destination[i]);
        }
    }

    [Fact]
    public void FastLanesBitUnpacker_DefaultInstance_ImplementsInterface()
    {
        IFastLanesBitUnpacker instance = FastLanesBitUnpacker.Default;
        byte[] packed = [0x55, 0x33];
        Span<uint> unpacked = stackalloc uint[16];

        instance.Unpack16Lanes(packed, unpacked, bitWidth: 1);

        Assert.Equal(1u, unpacked[0]);
        Assert.Equal(0u, unpacked[1]);
        Assert.Equal(1u, unpacked[2]);
        Assert.Equal(0u, unpacked[3]);
    }

    [Fact]
    public void Unpack16_Vector512_ExplicitTierTest_MatchesOracle()
    {
        if (!Vector512.IsHardwareAccelerated || !Avx512F.IsSupported)
            return;

        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes + 64];
            var rng = new Random(10000 + w);
            rng.NextBytes(packed);

            FastLanesBitUnpacker.Unpack16_Vector512(packed.AsSpan(0, requiredBytes), actual, w, requiredBytes);
            ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w);

            for (int i = 0; i < 16; i++)
            {
                Assert.True(expected[i] == actual[i], $"AVX-512 tier failed at width {w}, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }
        }
    }

    [Fact]
    public void Unpack16_Vector256_ExplicitTierTest_MatchesOracle()
    {
        if (!Vector256.IsHardwareAccelerated || !Avx2.IsSupported)
            return;

        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes + 64];
            var rng = new Random(20000 + w);
            rng.NextBytes(packed);

            FastLanesBitUnpacker.Unpack16_Vector256(packed.AsSpan(0, requiredBytes), actual, w, requiredBytes);
            ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w);

            for (int i = 0; i < 16; i++)
            {
                Assert.True(expected[i] == actual[i], $"AVX2 tier failed at width {w}, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }
        }
    }

    [Fact]
    public void Unpack16_Scalar_ExplicitTierTest_MatchesOracle()
    {
        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes];
            var rng = new Random(30000 + w);
            rng.NextBytes(packed);

            FastLanesBitUnpacker.Unpack16_Scalar(packed.AsSpan(0, requiredBytes), actual, w);
            ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w);

            for (int i = 0; i < 16; i++)
            {
                Assert.True(expected[i] == actual[i], $"Scalar tier failed at width {w}, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }
        }
    }

    [Fact]
    public void Unpack16Lanes_ArbitraryLaneOffsets_MatchesOracle()
    {
        IFastLanesBitUnpacker unpacker = FastLanesBitUnpacker.Default;
        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        int[] testWidths = [1, 2, 3, 4, 5, 7, 8, 9, 11, 13, 16, 17, 24, 31, 32];
        int[] testLaneOffsets = [0, 1, 2, 3, 5, 7, 8, 15, 16, 17, 31, 32, 47, 48];

        for (int wIdx = 0; wIdx < testWidths.Length; wIdx++)
        {
            int w = testWidths[wIdx];
            for (int oIdx = 0; oIdx < testLaneOffsets.Length; oIdx++)
            {
                int laneOffset = testLaneOffsets[oIdx];
                int totalBits = (laneOffset + 16) * w;
                int reqBytes = (totalBits + 7) >> 3;
                byte[] buffer = new byte[reqBytes + 16];
                var rng = new Random(40000 + w * 100 + laneOffset);
                rng.NextBytes(buffer);

                unpacker.Unpack16Lanes(buffer.AsSpan(0, reqBytes), actual, w, laneOffset);

                ReferenceOracleUnpack16WithBitOffset(buffer, expected, w, laneOffset * w);

                for (int i = 0; i < 16; i++)
                {
                    Assert.True(expected[i] == actual[i],
                        $"Mismatch at width {w}, laneOffset {laneOffset}, lane {i}: expected {expected[i]}, actual {actual[i]}");
                }
            }
        }
    }

    private static void ReferenceOracleUnpack16WithBitOffset(ReadOnlySpan<byte> packed, Span<uint> unpacked, int bitWidth, int initialBitOffset)
    {
        uint mask = bitWidth == 32 ? uint.MaxValue : ((1u << bitWidth) - 1u);
        int bitOffset = initialBitOffset;

        for (int lane = 0; lane < 16 && lane < unpacked.Length; lane++)
        {
            int byteStart = bitOffset >> 3;
            int bitInByte = bitOffset & 7;

            ulong accumulator = 0;
            int bytesLeft = packed.Length - byteStart;
            for (int b = 0; b < 8 && b < bytesLeft; b++)
            {
                accumulator |= ((ulong)packed[byteStart + b]) << (b * 8);
            }

            unpacked[lane] = (uint)((accumulator >> bitInByte) & mask);
            bitOffset += bitWidth;
        }
    }

    [Fact]
    public void Unpack16Lanes_LaneOffsetValidation_NegativeOffsetThrows()
    {
        IFastLanesBitUnpacker unpacker = FastLanesBitUnpacker.Default;
        byte[] packed = new byte[64];
        uint[] dest = new uint[16];

        Assert.Throws<ArgumentOutOfRangeException>(() => unpacker.Unpack16Lanes(packed, dest, bitWidth: 4, laneOffset: -1));
    }
}
