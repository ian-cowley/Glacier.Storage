namespace Glacier.Storage.Tests;

using System;
using System.Runtime.Intrinsics.X86;
using Glacier.Storage.Encoding;
using Xunit;

public class FastLanesEmpiricalStressTests
{
    private static void ReferenceOracleUnpack16(ReadOnlySpan<byte> packed, Span<uint> unpacked, int bitWidth, int initialBitOffset = 0)
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
    public void Unpack16_Vector256_ExhaustiveAll32Widths_MultiplePatterns_MatchesOracle()
    {
        if (!Avx2.IsSupported) return;

        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes + 64];

            // 1. All zeros
            Array.Clear(packed);
            FastLanesBitUnpacker.Unpack16_Vector256(packed.AsSpan(0, requiredBytes), actual, w, requiredBytes);
            ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w);
            for (int i = 0; i < 16; i++)
            {
                Assert.True(expected[i] == actual[i], $"Width {w} AllZeros, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }

            // 2. All ones
            Array.Fill(packed, (byte)0xFF);
            FastLanesBitUnpacker.Unpack16_Vector256(packed.AsSpan(0, requiredBytes), actual, w, requiredBytes);
            ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w);
            for (int i = 0; i < 16; i++)
            {
                Assert.True(expected[i] == actual[i], $"Width {w} AllOnes, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }

            // 3. Alternating 0xAA / 0x55
            for (int b = 0; b < packed.Length; b++) packed[b] = (b % 2 == 0) ? (byte)0xAA : (byte)0x55;
            FastLanesBitUnpacker.Unpack16_Vector256(packed.AsSpan(0, requiredBytes), actual, w, requiredBytes);
            ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w);
            for (int i = 0; i < 16; i++)
            {
                Assert.True(expected[i] == actual[i], $"Width {w} Alternating, lane {i}: expected {expected[i]}, actual {actual[i]}");
            }

            // 4. 20 random seeds
            for (int seed = 0; seed < 20; seed++)
            {
                var rng = new Random(seed * 100 + w);
                rng.NextBytes(packed);
                FastLanesBitUnpacker.Unpack16_Vector256(packed.AsSpan(0, requiredBytes), actual, w, requiredBytes);
                ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w);
                for (int i = 0; i < 16; i++)
                {
                    Assert.True(expected[i] == actual[i], $"Width {w} RandomSeed {seed}, lane {i}: expected {expected[i]}, actual {actual[i]}");
                }
            }
        }
    }

    [Fact]
    public void Unpack16_Scalar_ExhaustiveAll32Widths_ArbitraryBitOffsets_MatchesOracle()
    {
        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            for (int bitOffset = 0; bitOffset < 16; bitOffset++)
            {
                int requiredBytes = ((16 * w + bitOffset) + 7) >> 3;
                byte[] packed = new byte[requiredBytes + 16];
                var rng = new Random(777 + w * 16 + bitOffset);
                rng.NextBytes(packed);

                FastLanesBitUnpacker.Unpack16_Scalar(packed.AsSpan(0, requiredBytes), actual, w, bitOffset);
                ReferenceOracleUnpack16(packed.AsSpan(0, requiredBytes), expected, w, bitOffset);

                for (int i = 0; i < 16; i++)
                {
                    Assert.True(expected[i] == actual[i], $"Scalar Width {w} bitOffset {bitOffset}, lane {i}: expected {expected[i]}, actual {actual[i]}");
                }
            }
        }
    }

    [Fact]
    public void Unpack16Lanes_ArbitraryUnalignedLaneOffsets_AllWidths_MatchesOracle()
    {
        IFastLanesBitUnpacker unpacker = FastLanesBitUnpacker.Default;
        int[] testLaneOffsets = [0, 1, 2, 3, 5, 7, 9, 11, 13, 15, 17, 23, 31, 37, 49, 63];

        Span<uint> actual = stackalloc uint[16];
        Span<uint> expected = stackalloc uint[16];

        for (int w = 1; w <= 32; w++)
        {
            uint mask = w == 32 ? uint.MaxValue : ((1u << w) - 1u);
            const int totalLanes = 128;
            int totalBytes = (totalLanes * w + 7) >> 3;
            byte[] packed = new byte[totalBytes + 16];

            // Fill with deterministic values
            uint[] allValues = new uint[totalLanes];
            for (int i = 0; i < totalLanes; i++)
            {
                allValues[i] = (uint)((i * 37 + 13) & mask);
                // Pack value
                int bitOffset = i * w;
                for (int b = 0; b < w; b++)
                {
                    if (((allValues[i] >> b) & 1) != 0)
                    {
                        packed[(bitOffset + b) >> 3] |= (byte)(1 << ((bitOffset + b) & 7));
                    }
                }
            }

            foreach (int laneOffset in testLaneOffsets)
            {
                if (laneOffset + 16 > totalLanes) continue;

                unpacker.Unpack16Lanes(packed, actual, w, laneOffset);

                for (int i = 0; i < 16; i++)
                {
                    uint exp = allValues[laneOffset + i];
                    Assert.True(exp == actual[i], $"Unpack16Lanes width {w}, laneOffset {laneOffset}, lane {i}: expected {exp}, actual {actual[i]}");
                }
            }
        }
    }

    [Fact]
    public void Unpack16Lanes_NegativeOffsetOrInvalidBitWidth_Throws()
    {
        IFastLanesBitUnpacker unpacker = FastLanesBitUnpacker.Default;
        byte[] packed = new byte[64];
        uint[] dest = new uint[16];

        Assert.Throws<ArgumentOutOfRangeException>(() => unpacker.Unpack16Lanes(packed, dest, bitWidth: 16, laneOffset: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => unpacker.Unpack16Lanes(packed, dest, bitWidth: 0, laneOffset: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => unpacker.Unpack16Lanes(packed, dest, bitWidth: 33, laneOffset: 0));
        Assert.Throws<ArgumentException>(() => unpacker.Unpack16Lanes(packed, new uint[15], bitWidth: 16, laneOffset: 0));
        Assert.Throws<ArgumentException>(() => unpacker.Unpack16Lanes(new byte[1], dest, bitWidth: 16, laneOffset: 0));
    }

    [Fact]
    public void Unpack16_CanaryPreservation_NoBufferOverrun()
    {
        const uint canary = 0xDEADBEEF;
        Span<uint> dest = stackalloc uint[32];

        for (int w = 1; w <= 32; w++)
        {
            dest.Fill(canary);
            int requiredBytes = (16 * w + 7) >> 3;
            byte[] packed = new byte[requiredBytes + 64];
            Array.Fill(packed, (byte)0x77);

            FastLanesBitUnpacker.Unpack16(packed.AsSpan(0, requiredBytes), dest, w);

            for (int i = 16; i < 32; i++)
            {
                Assert.True(dest[i] == canary, $"Canary corrupted at index {i} for width {w}");
            }
        }
    }
}
