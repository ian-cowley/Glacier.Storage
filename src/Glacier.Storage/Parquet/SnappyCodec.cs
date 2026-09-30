namespace Glacier.Storage.Parquet;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// Pure C# high-performance Snappy compressor and decompressor.
/// Employs 64-bit unaligned memory copies (ulong blits) and 4-byte hash match finding.
/// Zero external dependencies, trim-safe, and Native AOT compatible.
/// </summary>
public static unsafe class SnappyCodec
{
    private const int MaxHashTableSize = 16384;
    private const int Shift = 32 - 14;

    public static int GetMaxCompressedLength(int sourceLength)
    {
        return 32 + sourceLength + (sourceLength / 6);
    }

    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            destination[0] = 0;
            return 1;
        }

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            byte* dst = pDst;
            byte* src = pSrc;
            byte* srcEnd = src + source.Length;

            // Write varint uncompressed length
            uint uncompressedLen = (uint)source.Length;
            while (uncompressedLen >= 0x80)
            {
                *dst++ = (byte)((uncompressedLen & 0x7F) | 0x80);
                uncompressedLen >>= 7;
            }
            *dst++ = (byte)uncompressedLen;

            if (source.Length < 16)
            {
                EmitLiteral(ref dst, src, source.Length);
                return (int)(dst - pDst);
            }

            // Stack-allocated hash table (16384 entries * 2 bytes = 32 KB)
            ushort* table = stackalloc ushort[MaxHashTableSize];
            Unsafe.InitBlock(table, 0, MaxHashTableSize * sizeof(ushort));

            byte* anchor = src;
            byte* limit = srcEnd - 4;

            while (src <= limit)
            {
                uint val = Unsafe.ReadUnaligned<uint>(src);
                uint hash = (val * 0x1e35a7bd) >> Shift;

                byte* match = pSrc + table[hash];
                table[hash] = (ushort)(src - pSrc);

                if (match < src && (src - match) < 65535 && Unsafe.ReadUnaligned<uint>(match) == val)
                {
                    // Emit pending literals
                    int litLen = (int)(src - anchor);
                    if (litLen > 0)
                    {
                        EmitLiteral(ref dst, anchor, litLen);
                    }

                    // Determine match length
                    src += 4;
                    match += 4;
                    while (src < srcEnd && *src == *match)
                    {
                        src++;
                        match++;
                    }

                    int matchLen = (int)(src - (pSrc + table[hash]));
                    int offset = (int)(src - match);

                    EmitCopy(ref dst, offset, matchLen);
                    anchor = src;
                    continue;
                }

                src++;
            }

            // Emit final literals
            if (anchor < srcEnd)
            {
                EmitLiteral(ref dst, anchor, (int)(srcEnd - anchor));
            }

            return (int)(dst - pDst);
        }
    }

    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty) return 0;

        fixed (byte* pSrc = source)
        fixed (byte* pDst = destination)
        {
            byte* src = pSrc;
            byte* srcEnd = src + source.Length;
            byte* dst = pDst;
            byte* dstEnd = dst + destination.Length;

            // Read varint uncompressed length
            uint uncompressedLen = 0;
            int shift = 0;
            while (src < srcEnd)
            {
                byte b = *src++;
                uncompressedLen |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }

            while (src < srcEnd && dst < dstEnd)
            {
                byte tag = *src++;
                int tagType = tag & 0x03;

                if (tagType == 0) // Literal
                {
                    int len = (tag >> 2) + 1;
                    if (len > 60)
                    {
                        int extraBytes = len - 60;
                        len = 0;
                        for (int i = 0; i < extraBytes && src < srcEnd; i++)
                        {
                            len |= *src++ << (i * 8);
                        }
                        len += 1;
                    }

                    // Copy literal
                    int toCopy = Math.Min(len, (int)(dstEnd - dst));
                    CopyBytesFast(dst, src, toCopy);
                    src += len;
                    dst += toCopy;
                }
                else if (tagType == 1) // Copy 1-byte offset
                {
                    int len = ((tag >> 2) & 7) + 4;
                    int offset = ((tag >> 5) << 8) | *src++;
                    byte* match = dst - offset;

                    int toCopy = Math.Min(len, (int)(dstEnd - dst));
                    CopyOverlapping(dst, match, toCopy, offset);
                    dst += toCopy;
                }
                else if (tagType == 2) // Copy 2-byte offset
                {
                    int len = (tag >> 2) + 1;
                    int offset = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(src, 2));
                    src += 2;
                    byte* match = dst - offset;

                    int toCopy = Math.Min(len, (int)(dstEnd - dst));
                    CopyOverlapping(dst, match, toCopy, offset);
                    dst += toCopy;
                }
                else // Copy 4-byte offset
                {
                    int len = (tag >> 2) + 1;
                    int offset = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(src, 4));
                    src += 4;
                    byte* match = dst - offset;

                    int toCopy = Math.Min(len, (int)(dstEnd - dst));
                    CopyOverlapping(dst, match, toCopy, offset);
                    dst += toCopy;
                }
            }

            return (int)(dst - pDst);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitLiteral(ref byte* dst, byte* src, int length)
    {
        int n = length - 1;
        if (n < 60)
        {
            *dst++ = (byte)(n << 2);
        }
        else if (n < 256)
        {
            *dst++ = 60 << 2;
            *dst++ = (byte)n;
        }
        else
        {
            *dst++ = 61 << 2;
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(dst, 2), (ushort)n);
            dst += 2;
        }

        CopyBytesFast(dst, src, length);
        dst += length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitCopy(ref byte* dst, int offset, int length)
    {
        while (length > 0)
        {
            int toCopy = Math.Min(length, 64);
            if (toCopy >= 4 && toCopy <= 11 && offset < 2048)
            {
                byte tag = (byte)(1 | ((toCopy - 4) << 2) | ((offset >> 8) << 5));
                *dst++ = tag;
                *dst++ = (byte)(offset & 0xFF);
            }
            else
            {
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
