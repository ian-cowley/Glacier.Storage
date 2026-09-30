namespace Glacier.Storage.Parquet;

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static class ParquetRleCodec
{
    public static int Decode(ReadOnlySpan<byte> source, int bitWidth, Span<uint> destination)
    {
        if (bitWidth == 0)
        {
            destination.Clear();
            return destination.Length;
        }

        int srcOffset = 0;
        int destOffset = 0;
        int totalDest = destination.Length;

        while (srcOffset < source.Length && destOffset < totalDest)
        {
            uint header = ReadVarint(source, ref srcOffset);
            if ((header & 1) == 0)
            {
                // RLE run
                int count = (int)(header >> 1);
                int valBytes = (bitWidth + 7) / 8;
                uint val = 0;
                for (int b = 0; b < valBytes && srcOffset < source.Length; b++)
                {
                    val |= (uint)source[srcOffset++] << (b * 8);
                }

                int toFill = Math.Min(count, totalDest - destOffset);
                destination.Slice(destOffset, toFill).Fill(val);
                destOffset += toFill;
            }
            else
            {
                // Bit-packed run
                int numGroups = (int)(header >> 1);
                int count = numGroups * 8;
                int byteLen = numGroups * bitWidth;

                if (srcOffset + byteLen > source.Length)
                {
                    byteLen = source.Length - srcOffset;
                }

                ReadOnlySpan<byte> packed = source.Slice(srcOffset, byteLen);
                srcOffset += byteLen;

                int toUnpack = Math.Min(count, totalDest - destOffset);
                VectorizedBitUnpacker.Unpack(packed, bitWidth, destination.Slice(destOffset, toUnpack));
                destOffset += toUnpack;
            }
        }

        return destOffset;
    }

    public static int Encode(ReadOnlySpan<uint> values, int bitWidth, Span<byte> destination)
    {
        if (values.IsEmpty) return 0;

        int destOffset = 0;
        int srcOffset = 0;
        int total = values.Length;

        // Group into bit-packed runs of 8 values
        while (srcOffset < total)
        {
            int remaining = total - srcOffset;
            int groups = Math.Min(64, (remaining + 7) / 8);
            int count = Math.Min(remaining, groups * 8);

            // Bit-packed run header: (groups << 1) | 1
            uint header = ((uint)groups << 1) | 1u;
            WriteVarint(destination, ref destOffset, header);

            // Pack values
            int byteLen = groups * bitWidth;
            Span<byte> packDest = destination.Slice(destOffset, byteLen);
            packDest.Clear();

            int bitOffset = 0;
            for (int i = 0; i < count; i++)
            {
                uint val = values[srcOffset + i];
                int byteIdx = bitOffset >> 3;
                int bitInByte = bitOffset & 7;

                // Write bits
                for (int b = 0; b < bitWidth; b++)
                {
                    if (((val >> b) & 1) != 0)
                    {
                        int curByte = (bitOffset + b) >> 3;
                        int curBit = (bitOffset + b) & 7;
                        if (curByte < packDest.Length)
                        {
                            packDest[curByte] |= (byte)(1 << curBit);
                        }
                    }
                }
                bitOffset += bitWidth;
            }

            destOffset += byteLen;
            srcOffset += count;
        }

        return destOffset;
    }

    private static uint ReadVarint(ReadOnlySpan<byte> span, ref int offset)
    {
        uint res = 0;
        int shift = 0;
        while (offset < span.Length && shift < 32)
        {
            byte b = span[offset++];
            res |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return res;
            shift += 7;
        }
        return res;
    }

    private static void WriteVarint(Span<byte> span, ref int offset, uint val)
    {
        while ((val & ~0x7Fu) != 0)
        {
            span[offset++] = (byte)((val & 0x7F) | 0x80);
            val >>= 7;
        }
        span[offset++] = (byte)val;
    }
}
