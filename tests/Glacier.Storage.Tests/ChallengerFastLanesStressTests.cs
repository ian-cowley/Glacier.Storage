namespace Glacier.Storage.Tests;

using System;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using Glacier.Storage.Encoding;
using Xunit;

public class ChallengerFastLanesStressTests
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

    [Fact]
    public void Unpack16_ExhaustiveAll32BitWidths_MatchesOracle()
    {
        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes];
            var rng = new Random(1000 + w);
            rng.NextBytes(packed);

            FastLanesBitUnpacker.Unpack16(packed, actual, w);
            ReferenceOracleUnpack16(packed, expected, w);

            for (int i = 0; i < 16; i++)
            {
                Assert.True(expected[i] == actual[i], $"Mismatch at width {w}, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }
        }
    }

    [Fact]
    public void Unpack8Lanes64_ExhaustiveAll64BitWidths_MatchesOracle()
    {
        Span<ulong> actual = stackalloc ulong[8];
        Span<ulong> expected = stackalloc ulong[8];

        for (int w = 1; w <= 64; w++)
        {
            int requiredBytes = (8 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes];
            var rng = new Random(2000 + w);
            rng.NextBytes(packed);

            FastLanesBitUnpacker.Unpack8Lanes64(packed, actual, w);
            ReferenceOracleUnpack8_64(packed, expected, w);

            for (int i = 0; i < 8; i++)
            {
                Assert.True(expected[i] == actual[i], $"Mismatch at width {w}, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }
        }
    }

    [Fact]
    public void Unpack16_ExactMinimumBuffer_NoOverreadOrCrash()
    {
        Span<uint> actual = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes];
            Array.Fill(packed, (byte)0xFF);

            // Slicing exactly to requiredBytes ensures no padding bytes exist
            FastLanesBitUnpacker.Unpack16(packed.AsSpan(0, requiredBytes), actual, w);

            uint expectedMask = w == 32 ? uint.MaxValue : ((1u << w) - 1u);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(expectedMask, actual[i]);
            }
        }
    }

    [Fact]
    public void Unpack8Lanes64_ExactMinimumBuffer_NoOverreadOrCrash()
    {
        Span<ulong> actual = stackalloc ulong[8];

        for (int w = 1; w <= 64; w++)
        {
            int requiredBytes = (8 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes];
            Array.Fill(packed, (byte)0xFF);

            FastLanesBitUnpacker.Unpack8Lanes64(packed.AsSpan(0, requiredBytes), actual, w);

            ulong expectedMask = w == 64 ? ulong.MaxValue : ((1UL << w) - 1UL);
            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(expectedMask, actual[i]);
            }
        }
    }

    [Fact]
    public void Unpack16_UnalignedOffsets_AllOffsetsMatchOracle()
    {
        byte[] buffer = new byte[256];
        var rng = new Random(4242);
        rng.NextBytes(buffer);

        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        int[] offsets = [0, 1, 2, 3, 5, 7, 11, 13, 17, 19, 23, 31];

        for (int w = 1; w <= 32; w++)
        {
            int reqBytes = (16 * w + 7) >> 3;
            foreach (int off in offsets)
            {
                if (off + reqBytes <= buffer.Length)
                {
                    FastLanesBitUnpacker.Unpack16(buffer.AsSpan(off, reqBytes), actual, w);
                    ReferenceOracleUnpack16(buffer.AsSpan(off, reqBytes), expected, w);

                    for (int i = 0; i < 16; i++)
                    {
                        Assert.Equal(expected[i], actual[i]);
                    }
                }
            }
        }
    }

    [Fact]
    public void Unpack16_ScalarFallback_ExhaustiveAll32BitWidths_MatchesOracle()
    {
        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes];
            var rng = new Random(3000 + w);
            rng.NextBytes(packed);

            FastLanesBitUnpacker.Unpack16_Scalar(packed, actual, w);
            ReferenceOracleUnpack16(packed, expected, w);

            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }
    }

    [Fact]
    public void Unpack8Lanes64_ScalarFallback_ExhaustiveAll64BitWidths_MatchesOracle()
    {
        Span<ulong> actual = stackalloc ulong[8];
        Span<ulong> expected = stackalloc ulong[8];

        for (int w = 1; w <= 64; w++)
        {
            int requiredBytes = (8 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes];
            var rng = new Random(4000 + w);
            rng.NextBytes(packed);

            FastLanesBitUnpacker.Unpack8_64_Scalar(packed, actual, w);
            ReferenceOracleUnpack8_64(packed, expected, w);

            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }
    }

    private delegate void Unpack16Vector256Delegate(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int requiredBytes);

    [Fact]
    public void Unpack16_Vector256_FallbackParityVsOracle()
    {
        if (!Avx2.IsSupported)
        {
            return; // Skip if AVX2 not supported on host
        }

        var method = typeof(FastLanesBitUnpacker).GetMethod("Unpack16_Vector256", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var unpack256 = method.CreateDelegate<Unpack16Vector256Delegate>();

        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes + 64];
            var rng = new Random(5000 + w);
            rng.NextBytes(packed);

            unpack256(packed.AsSpan(0, requiredBytes), actual, w, requiredBytes);
            ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w);

            for (int i = 0; i < 16; i++)
            {
                Assert.True(expected[i] == actual[i], $"AVX2 fallback failed at bitWidth {w}, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }
        }
    }

    [Fact]
    public void Unpack16Lanes_LaneOffsetNotAlignedToByte_FailsOrMisaligns()
    {
        IFastLanesBitUnpacker unpacker = FastLanesBitUnpacker.Default;
        // Pack 32 3-bit values: 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3 ...
        byte[] packed = new byte[32];
        int bitOffset = 0;
        for (int i = 0; i < 32; i++)
        {
            uint val = (uint)(i % 8);
            for (int b = 0; b < 3; b++)
            {
                if (((val >> b) & 1) != 0)
                {
                    packed[(bitOffset + b) >> 3] |= (byte)(1 << ((bitOffset + b) & 7));
                }
            }
            bitOffset += 3;
        }

        Span<uint> dest = stackalloc uint[16];
        // Unpack starting at laneOffset = 1 (bitOffset = 3)
        // If byteOffset = (1 * 3) >> 3 = 0, Unpack16 reads from bit 0, yielding val[0]=0 instead of val[1]=1!
        unpacker.Unpack16Lanes(packed, dest, bitWidth: 3, laneOffset: 1);

        // Expected lane 0 to be value at index 1 (which is 1)
        Assert.Equal(1u, dest[0]);
    }
}

