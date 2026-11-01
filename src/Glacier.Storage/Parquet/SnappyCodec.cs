namespace Glacier.Storage.Parquet;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

/// <summary>
/// Pure C# high-performance Snappy compressor and decompressor.
/// Employs 64-bit unaligned memory copies (ulong blits), dynamic hash sizing, and strict bounds verification.
/// Zero external dependencies, trim-safe, and Native AOT compatible.
/// </summary>
public static unsafe class SnappyCodec
{
    private const int MinHashTableSize = 256;
    private const int MaxHashTableSize = 16384;

    public static int GetMaxCompressedLength(int sourceLength)
    {
        if (sourceLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLength), "Source length cannot be negative.");
        }

        long max = 32L + sourceLength + (sourceLength / 6);
        if (max > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLength), "Source length exceeds maximum Snappy block capacity.");
        }

        return (int)max;
    }

    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            if (destination.Length < 1)
            {
                throw new ArgumentException("Destination buffer is too small.", nameof(destination));
            }
            destination[0] = 0;
            return 1;
        }

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            byte* dst = pDst;
            byte* dstEnd = pDst + destination.Length;
            byte* src = pSrc;
            byte* srcEnd = src + source.Length;

            // Write varint uncompressed length
            uint uncompressedLen = (uint)source.Length;
            while (uncompressedLen >= 0x80)
            {
                if (dst >= dstEnd)
                {
                    throw new ArgumentException("Destination buffer is too small.", nameof(destination));
                }
                *dst++ = (byte)((uncompressedLen & 0x7F) | 0x80);
                uncompressedLen >>= 7;
            }
            if (dst >= dstEnd)
            {
                throw new ArgumentException("Destination buffer is too small.", nameof(destination));
            }
            *dst++ = (byte)uncompressedLen;

            if (source.Length < 16)
            {
                EmitLiteral(ref dst, dstEnd, src, source.Length);
                return (int)(dst - pDst);
            }

            // Dynamic power-of-2 stack-allocated hash table (256..16384 entries = 1 KB..64 KB)
            int hashTableSize = MinHashTableSize;
            while (hashTableSize < MaxHashTableSize && hashTableSize < source.Length)
            {
                hashTableSize <<= 1;
            }
            int shift = 32 - BitOperations.Log2((uint)hashTableSize);

            int* table = stackalloc int[hashTableSize];
            Unsafe.InitBlock(table, 0xFF, (uint)(hashTableSize * sizeof(int))); // initialize to -1

            byte* anchor = src;
            byte* limit = srcEnd - 4;

            while (src <= limit)
            {
                uint val = Unsafe.ReadUnaligned<uint>(src);
                uint hash = (val * 0x1e35a7bd) >> shift;

                int prevOffset = table[hash];
                int curOffset = (int)(src - pSrc);
                table[hash] = curOffset;

                int candidateDistance = curOffset - prevOffset;
                if (prevOffset >= 0 && candidateDistance > 0 && candidateDistance <= 65535)
                {
                    byte* match = pSrc + prevOffset;
                    if (Unsafe.ReadUnaligned<uint>(match) == val)
                    {
                        // Emit pending literals
                        int litLen = (int)(src - anchor);
                        if (litLen > 0)
                        {
                            EmitLiteral(ref dst, dstEnd, anchor, litLen);
                        }

                        // Determine match length
                        byte* matchStart = match;
                        byte* srcStart = src;
                        src += 4;
                        match += 4;
                        while (src < srcEnd && *src == *match)
                        {
                            src++;
                            match++;
                        }

                        int matchLen = (int)(src - srcStart);
                        int offset = (int)(srcStart - matchStart);

                        EmitCopy(ref dst, dstEnd, offset, matchLen);
                        anchor = src;
                        continue;
                    }
                }

                src++;
            }

            // Emit final literals
            if (anchor < srcEnd)
            {
                EmitLiteral(ref dst, dstEnd, anchor, (int)(srcEnd - anchor));
            }

            return (int)(dst - pDst);
        }
    }

    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            return 0;
        }

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            byte* src = pSrc;
            byte* srcEnd = src + source.Length;

            // Read 32-bit varint uncompressed length (at most 5 bytes)
            uint uncompressedLen = 0;
            int shift = 0;
            bool headerComplete = false;
            while (src < srcEnd)
            {
                byte b = *src++;
                if (shift == 28 && (b & 0xF0) != 0)
                {
                    throw new InvalidDataException("Snappy varint length overflows UInt32.");
                }

                uncompressedLen |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    headerComplete = true;
                    break;
                }

                shift += 7;
                if (shift > 28)
                {
                    throw new InvalidDataException("Malformed Snappy varint header.");
                }
            }

            if (!headerComplete || uncompressedLen > (uint)int.MaxValue)
            {
                throw new InvalidDataException("Truncated or oversized Snappy varint header.");
            }

            if ((uint)destination.Length < uncompressedLen)
            {
                throw new ArgumentException(
                    $"Destination buffer ({destination.Length} bytes) is smaller than Snappy uncompressed length ({uncompressedLen} bytes).",
                    nameof(destination));
            }

            byte* dst = pDst;
            byte* targetEnd = pDst + uncompressedLen;

            while (src < srcEnd)
            {
                byte tag = *src++;
                int tagType = tag & 0x03;

                if (tagType == 0) // Literal
                {
                    uint lenMinus1 = (uint)(tag >> 2);
                    if (lenMinus1 >= 60)
                    {
                        int extraBytes = (int)(lenMinus1 - 59); // 1, 2, 3, or 4 bytes
                        if (srcEnd - src < extraBytes)
                        {
                            throw new InvalidDataException("Truncated Snappy literal length.");
                        }

                        lenMinus1 = 0;
                        for (int i = 0; i < extraBytes; i++)
                        {
                            lenMinus1 |= (uint)(*src++) << (i * 8);
                        }
                    }

                    ulong litLen64 = (ulong)lenMinus1 + 1UL;
                    if (litLen64 > (ulong)(srcEnd - src) || litLen64 > (ulong)(targetEnd - dst))
                    {
                        throw new InvalidDataException("Snappy literal exceeds source or destination bounds.");
                    }

                    int litLen = (int)litLen64;
                    CopyBytesFast(dst, src, litLen);
                    src += litLen;
                    dst += litLen;
                }
                else if (tagType == 1) // Copy with 1-byte offset (11-bit total offset)
                {
                    if (src >= srcEnd)
                    {
                        throw new InvalidDataException("Truncated Snappy 1-byte copy offset.");
                    }

                    int len = ((tag >> 2) & 0x07) + 4;
                    uint offset = (uint)(((tag >> 5) << 8) | *src++);
                    if (offset == 0 || offset > (uint)(dst - pDst) || len > (targetEnd - dst))
                    {
                        throw new InvalidDataException("Invalid Snappy 1-byte copy offset or length.");
                    }

                    CopyOverlapping(dst, dst - offset, len, (int)offset);
                    dst += len;
                }
                else if (tagType == 2) // Copy with 2-byte offset (16-bit LE)
                {
                    if (srcEnd - src < 2)
                    {
                        throw new InvalidDataException("Truncated Snappy 2-byte copy offset.");
                    }

                    int len = (tag >> 2) + 1;
                    uint offset = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(src, 2));
                    src += 2;
                    if (offset == 0 || offset > (uint)(dst - pDst) || len > (targetEnd - dst))
                    {
                        throw new InvalidDataException("Invalid Snappy 2-byte copy offset or length.");
                    }

                    CopyOverlapping(dst, dst - offset, len, (int)offset);
                    dst += len;
                }
                else // Copy with 4-byte offset (32-bit LE unsigned)
                {
                    if (srcEnd - src < 4)
                    {
                        throw new InvalidDataException("Truncated Snappy 4-byte copy offset.");
                    }

                    int len = (tag >> 2) + 1;
                    uint offset = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(src, 4));
                    src += 4;
                    if (offset == 0 || offset > (uint)(dst - pDst) || len > (targetEnd - dst))
                    {
                        throw new InvalidDataException("Invalid Snappy 4-byte copy offset or length.");
                    }

                    CopyOverlapping(dst, dst - offset, len, (int)offset);
                    dst += len;
                }
            }

            int totalWritten = (int)(dst - pDst);
            if ((uint)totalWritten != uncompressedLen)
            {
                throw new InvalidDataException($"Snappy stream produced {totalWritten} bytes, expected {uncompressedLen} bytes.");
            }

            return totalWritten;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitLiteral(ref byte* dst, byte* dstEnd, byte* src, int length)
    {
        uint n = (uint)(length - 1);
        int headerBytes = n switch
        {
            < 60u => 1,
            < 0x100u => 2,
            < 0x10000u => 3,
            < 0x1000000u => 4,
            _ => 5
        };

        if ((long)(dstEnd - dst) < headerBytes + (long)length)
        {
            throw new ArgumentException("Destination buffer is too small.", "destination");
        }

        if (n < 60u)
        {
            *dst++ = (byte)(n << 2);
        }
        else if (n < 0x100u)
        {
            *dst++ = 60 << 2;
            *dst++ = (byte)n;
        }
        else if (n < 0x10000u)
        {
            *dst++ = 61 << 2;
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(dst, 2), (ushort)n);
            dst += 2;
        }
        else if (n < 0x1000000u)
        {
            *dst++ = 62 << 2;
            dst[0] = (byte)n;
            dst[1] = (byte)(n >> 8);
            dst[2] = (byte)(n >> 16);
            dst += 3;
        }
        else
        {
            *dst++ = 63 << 2;
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(dst, 4), n);
            dst += 4;
        }

        CopyBytesFast(dst, src, length);
        dst += length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCopy(ref byte* dst, byte* dstEnd, int offset, int length)
    {
        while (length > 0)
        {
            int toCopy = Math.Min(length, 64);
            if (toCopy >= 4 && toCopy <= 11 && offset < 2048)
            {
                if (dstEnd - dst < 2)
                {
                    throw new ArgumentException("Destination buffer is too small.", "destination");
                }
                byte tag = (byte)(1 | ((toCopy - 4) << 2) | ((offset >> 8) << 5));
                *dst++ = tag;
                *dst++ = (byte)(offset & 0xFF);
            }
            else
            {
                if (dstEnd - dst < 3)
                {
                    throw new ArgumentException("Destination buffer is too small.", "destination");
                }
                byte tag = (byte)(2 | ((toCopy - 1) << 2));
                *dst++ = tag;
                BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(dst, 2), (ushort)offset);
                dst += 2;
            }
            length -= toCopy;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyBytesFast(byte* dst, byte* src, int count)
    {
        int i = 0;
        while (i <= count - 8)
        {
            Unsafe.WriteUnaligned(dst + i, Unsafe.ReadUnaligned<ulong>(src + i));
            i += 8;
        }
        while (i < count)
        {
            dst[i] = src[i];
            i++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyOverlapping(byte* dst, byte* src, int count, int offset)
    {
        if (offset >= 8)
        {
            CopyBytesFast(dst, src, count);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                dst[i] = src[i];
            }
        }
    }
}
