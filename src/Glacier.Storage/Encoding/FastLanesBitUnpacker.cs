namespace Glacier.Storage.Encoding;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

/// <summary>
/// Hardware-accelerated FastLanes SIMD bit-unpacking engine using Vector512, Vector256, and scalar fallbacks.
/// Unpacks 16 32-bit integer lanes or 8 64-bit integer lanes at peak hardware throughput.
/// </summary>
public sealed partial class FastLanesBitUnpacker : IFastLanesBitUnpacker
#if USE_LOCAL_FORMATS
    , Glacier.Formats.Encoding.IFastLanesBitUnpacker
#endif
{
    private static readonly FastLanesBitUnpacker s_default = new();

    /// <summary>
    /// Gets the default singleton <see cref="FastLanesBitUnpacker"/> instance.
    /// </summary>
    public static FastLanesBitUnpacker Default => s_default;

#if USE_LOCAL_FORMATS
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unpack16Lanes(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Default.Unpack16Lanes(packed, destination, bitWidth);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unpack16Lanes(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int laneOffset) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Default.Unpack16Lanes(packed, destination, bitWidth, laneOffset);

    /// <summary>
    /// Unpacks 16 32-bit lanes of the specified bit-width using the optimal hardware SIMD path.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 16 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack16(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack16(packed, destination, bitWidth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Unpack16_Vector512(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int requiredBytes) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack16_Vector512(packed, destination, bitWidth, requiredBytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Unpack16_Vector256(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int requiredBytes) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack16_Vector256(packed, destination, bitWidth, requiredBytes);

    /// <summary>
    /// Unpacks 16 32-bit lanes using scalar bit extraction.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 16 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Unpack16_Scalar(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack16_Scalar(packed, destination, bitWidth);

    /// <summary>
    /// Unpacks 16 32-bit lanes using scalar bit extraction starting at <paramref name="initialBitOffset"/>.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 16 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    /// <param name="initialBitOffset">The starting bit offset inside <paramref name="packed"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Unpack16_Scalar(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int initialBitOffset) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack16_Scalar(packed, destination, bitWidth, initialBitOffset);

    /// <summary>
    /// Unpacks 8 64-bit lanes of the specified bit-width using the optimal hardware SIMD path.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 8 unpacked 64-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 64).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack8Lanes64(ReadOnlySpan<byte> packed, Span<ulong> destination, int bitWidth) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack8Lanes64(packed, destination, bitWidth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Unpack8_64_Vector512(ReadOnlySpan<byte> packed, Span<ulong> destination, int bitWidth, int requiredBytes) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack8_64_Vector512(packed, destination, bitWidth, requiredBytes);

    /// <summary>
    /// Unpacks 8 64-bit lanes using scalar bit extraction.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 8 unpacked 64-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 64).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Unpack8_64_Scalar(ReadOnlySpan<byte> packed, Span<ulong> destination, int bitWidth) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack8_64_Scalar(packed, destination, bitWidth);

    /// <summary>
    /// Unpacks a contiguous block of 1,024 32-bit values (64 groups of 16 lanes).
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 1,024 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack1024(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth) =>
        Glacier.Formats.Encoding.FastLanesBitUnpacker.Unpack1024(packed, destination, bitWidth);
#else
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unpack16Lanes(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth)
    {
        Unpack16(packed, destination, bitWidth);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unpack16Lanes(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int laneOffset)
    {
        if (laneOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(laneOffset), "Lane offset cannot be negative.");

        if (bitWidth <= 0 || bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "Bit-width must be between 1 and 32.");

        if (destination.Length < 16)
            throw new ArgumentException("Destination span must have at least 16 elements.", nameof(destination));

        long totalStartBit = (long)laneOffset * bitWidth;
        long requiredBytesLong = ((totalStartBit + 16L * bitWidth) + 7) >> 3;
        if (packed.Length < requiredBytesLong)
            throw new ArgumentException($"Packed buffer is too short. Required: {requiredBytesLong}, Actual: {packed.Length}", nameof(packed));

        int byteOffset = checked((int)(totalStartBit >> 3));
        int bitRemainder = (int)(totalStartBit & 7);

        if (bitRemainder == 0)
        {
            Unpack16(packed.Slice(byteOffset), destination, bitWidth);
        }
        else
        {
            Unpack16_Scalar(packed.Slice(byteOffset), destination, bitWidth, bitRemainder);
        }
    }

    /// <summary>
    /// Unpacks 16 32-bit lanes of the specified bit-width using the optimal hardware SIMD path.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 16 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack16(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth)
    {
        if (bitWidth <= 0 || bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "Bit-width must be between 1 and 32.");

        if (destination.Length < 16)
            throw new ArgumentException("Destination span must have at least 16 elements.", nameof(destination));

        int requiredBytes = (16 * bitWidth + 7) >> 3;
        if (packed.Length < requiredBytes)
            throw new ArgumentException($"Packed buffer is too short. Required: {requiredBytes}, Actual: {packed.Length}", nameof(packed));

        if (bitWidth == 32)
        {
            MemoryMarshal.Cast<byte, uint>(packed[..64]).CopyTo(destination[..16]);
            return;
        }

        if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported)
        {
            Unpack16_Vector512(packed, destination, bitWidth, requiredBytes);
        }
        else if (Vector256.IsHardwareAccelerated && Avx2.IsSupported && bitWidth <= 16)
        {
            Unpack16_Vector256(packed, destination, bitWidth, requiredBytes);
        }
        else
        {
            Unpack16_Scalar(packed, destination, bitWidth);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Unpack16_Vector512(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int requiredBytes)
    {
        Vector512<uint> vPacked;
        if (packed.Length >= 64)
        {
            vPacked = Vector512.LoadUnsafe(ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(packed)));
        }
        else
        {
            Span<byte> pad = stackalloc byte[64];
            packed[..requiredBytes].CopyTo(pad);
            vPacked = Vector512.LoadUnsafe(ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(pad)));
        }

        ref readonly var table = ref s_bitWidthTables32[bitWidth];

        Vector512<uint> dwords = Avx512F.PermuteVar16x32(vPacked, table.DwordIndices);
        Vector512<uint> shifted = Avx512F.ShiftRightLogicalVariable(dwords, table.ShiftAmounts);

        if (table.HasStraddles)
        {
            Vector512<uint> dwordsNext = Avx512F.PermuteVar16x32(vPacked, table.DwordIndicesNext);
            Vector512<uint> shiftedNext = Avx512F.ShiftLeftLogicalVariable(dwordsNext, table.ShiftLeftAmounts);
            Vector512<uint> straddleBits = Vector512.BitwiseAnd(shiftedNext, table.StraddleMask);
            shifted = Vector512.BitwiseOr(shifted, straddleBits);
        }

        Vector512<uint> result = Vector512.BitwiseAnd(shifted, table.ValueMask);
        result.CopyTo(destination);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Unpack16_Vector256(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int requiredBytes)
    {
        if (bitWidth > 16)
        {
            Unpack16_Scalar(packed, destination, bitWidth);
            return;
        }

        Vector256<uint> vLow;
        if (packed.Length >= 32)
        {
            vLow = Vector256.LoadUnsafe(ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(packed)));
        }
        else
        {
            Span<byte> pad = stackalloc byte[32];
            packed[..requiredBytes].CopyTo(pad);
            vLow = Vector256.LoadUnsafe(ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(pad)));
        }

        ref readonly var table = ref s_bitWidthTables32[bitWidth];

        Vector256<uint> dwords0 = Avx2.PermuteVar8x32(vLow, table.DwordIndices.GetLower());
        Vector256<uint> shifted0 = Avx2.ShiftRightLogicalVariable(dwords0, table.ShiftAmounts.GetLower());
        if (table.HasStraddles)
        {
            Vector256<uint> dwordsNext0 = Avx2.PermuteVar8x32(vLow, table.DwordIndicesNext.GetLower());
            Vector256<uint> shiftedNext0 = Avx2.ShiftLeftLogicalVariable(dwordsNext0, table.ShiftLeftAmounts.GetLower());
            shifted0 = Vector256.BitwiseOr(shifted0, Vector256.BitwiseAnd(shiftedNext0, table.StraddleMask.GetLower()));
        }
        Vector256.BitwiseAnd(shifted0, table.ValueMask.GetLower()).CopyTo(destination[..8]);

        Vector256<uint> dwords1 = Avx2.PermuteVar8x32(vLow, table.DwordIndices.GetUpper());
        Vector256<uint> shifted1 = Avx2.ShiftRightLogicalVariable(dwords1, table.ShiftAmounts.GetUpper());
        if (table.HasStraddles)
        {
            Vector256<uint> dwordsNext1 = Avx2.PermuteVar8x32(vLow, table.DwordIndicesNext.GetUpper());
            Vector256<uint> shiftedNext1 = Avx2.ShiftLeftLogicalVariable(dwordsNext1, table.ShiftLeftAmounts.GetUpper());
            shifted1 = Vector256.BitwiseOr(shifted1, Vector256.BitwiseAnd(shiftedNext1, table.StraddleMask.GetUpper()));
        }
        Vector256.BitwiseAnd(shifted1, table.ValueMask.GetUpper()).CopyTo(destination.Slice(8, 8));
    }

    /// <summary>
    /// Unpacks 16 32-bit lanes using scalar bit extraction.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 16 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static unsafe void Unpack16_Scalar(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth)
    {
        Unpack16_Scalar(packed, destination, bitWidth, 0);
    }

    /// <summary>
    /// Unpacks 16 32-bit lanes using scalar bit extraction starting at <paramref name="initialBitOffset"/>.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 16 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    /// <param name="initialBitOffset">The starting bit offset inside <paramref name="packed"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static unsafe void Unpack16_Scalar(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int initialBitOffset)
    {
        if (bitWidth <= 0 || bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "Bit-width must be between 1 and 32.");
        if (initialBitOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(initialBitOffset), "Initial bit offset cannot be negative.");
        if (destination.Length < 16)
            throw new ArgumentException("Destination span must have at least 16 elements.", nameof(destination));

        long requiredBytes = ((long)initialBitOffset + (16L * bitWidth) + 7) >> 3;
        if (packed.Length < requiredBytes)
            throw new ArgumentException($"Packed buffer is too short. Required: {requiredBytes}, Actual: {packed.Length}", nameof(packed));

        uint mask = bitWidth == 32 ? uint.MaxValue : ((1u << bitWidth) - 1u);
        int bitOffset = initialBitOffset;

        fixed (byte* pSrc = packed)
        {
            for (int lane = 0; lane < 16; lane++)
            {
                int byteStart = bitOffset >> 3;
                int bitInByte = bitOffset & 7;

                ulong accumulator = 0;
                int bytesLeft = packed.Length - byteStart;
                for (int b = 0; b < 8 && b < bytesLeft; b++)
                {
                    accumulator |= ((ulong)pSrc[byteStart + b]) << (b * 8);
                }

                destination[lane] = (uint)((accumulator >> bitInByte) & mask);
                bitOffset += bitWidth;
            }
        }
    }

    /// <summary>
    /// Unpacks 8 64-bit lanes of the specified bit-width using the optimal hardware SIMD path.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 8 unpacked 64-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 64).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack8Lanes64(ReadOnlySpan<byte> packed, Span<ulong> destination, int bitWidth)
    {
        if (bitWidth <= 0 || bitWidth > 64)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "Bit-width must be between 1 and 64.");

        if (destination.Length < 8)
            throw new ArgumentException("Destination span must have at least 8 elements.", nameof(destination));

        int requiredBytes = (8 * bitWidth + 7) >> 3;
        if (packed.Length < requiredBytes)
            throw new ArgumentException($"Packed buffer too short. Required: {requiredBytes}, Actual: {packed.Length}", nameof(packed));

        if (bitWidth == 64)
        {
            MemoryMarshal.Cast<byte, ulong>(packed[..64]).CopyTo(destination[..8]);
            return;
        }

        if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported)
        {
            Unpack8_64_Vector512(packed, destination, bitWidth, requiredBytes);
        }
        else
        {
            Unpack8_64_Scalar(packed, destination, bitWidth);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Unpack8_64_Vector512(ReadOnlySpan<byte> packed, Span<ulong> destination, int bitWidth, int requiredBytes)
    {
        Vector512<ulong> vPacked;
        if (packed.Length >= 64)
        {
            vPacked = Vector512.LoadUnsafe(ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(packed)));
        }
        else
        {
            Span<byte> pad = stackalloc byte[64];
            packed[..requiredBytes].CopyTo(pad);
            vPacked = Vector512.LoadUnsafe(ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(pad)));
        }

        ref readonly var table = ref s_bitWidthTables64[bitWidth];

        Vector512<ulong> qwords = Avx512F.PermuteVar8x64(vPacked, table.QwordIndices);
        Vector512<ulong> shifted = Avx512F.ShiftRightLogicalVariable(qwords, table.ShiftAmounts);

        if (table.HasStraddles)
        {
            Vector512<ulong> qwordsNext = Avx512F.PermuteVar8x64(vPacked, table.QwordIndicesNext);
            Vector512<ulong> shiftedNext = Avx512F.ShiftLeftLogicalVariable(qwordsNext, table.ShiftLeftAmounts);
            Vector512<ulong> straddleBits = Vector512.BitwiseAnd(shiftedNext, table.StraddleMask);
            shifted = Vector512.BitwiseOr(shifted, straddleBits);
        }

        Vector512<ulong> result = Vector512.BitwiseAnd(shifted, table.ValueMask);
        result.CopyTo(destination);
    }

    /// <summary>
    /// Unpacks 8 64-bit lanes using scalar bit extraction.
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 8 unpacked 64-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 64).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static unsafe void Unpack8_64_Scalar(ReadOnlySpan<byte> packed, Span<ulong> destination, int bitWidth)
    {
        if (bitWidth <= 0 || bitWidth > 64)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), "Bit-width must be between 1 and 64.");
        if (destination.Length < 8)
            throw new ArgumentException("Destination span must have at least 8 elements.", nameof(destination));

        int requiredBytes = (8 * bitWidth + 7) >> 3;
        if (packed.Length < requiredBytes)
            throw new ArgumentException($"Packed buffer too short. Required: {requiredBytes}, Actual: {packed.Length}", nameof(packed));

        ulong mask = bitWidth == 64 ? ulong.MaxValue : ((1UL << bitWidth) - 1UL);
        int bitOffset = 0;

        fixed (byte* pSrc = packed)
        {
            for (int lane = 0; lane < 8; lane++)
            {
                int byteStart = bitOffset >> 3;
                int bitInByte = bitOffset & 7;

                ulong accumulator = 0;
                int bytesLeft = packed.Length - byteStart;
                for (int b = 0; b < 8 && b < bytesLeft; b++)
                {
                    accumulator |= ((ulong)pSrc[byteStart + b]) << (b * 8);
                }

                ulong val = (accumulator >> bitInByte) & mask;
                if (bitInByte + bitWidth > 64 && (byteStart + 8) < packed.Length)
                {
                    ulong high = (ulong)pSrc[byteStart + 8];
                    val |= (high << (64 - bitInByte)) & mask;
                }

                destination[lane] = val;
                bitOffset += bitWidth;
            }
        }
    }

    /// <summary>
    /// Unpacks a contiguous block of 1,024 32-bit values (64 groups of 16 lanes).
    /// </summary>
    /// <param name="packed">The bit-packed input bytes.</param>
    /// <param name="destination">The destination span receiving at least 1,024 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Unpack1024(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth)
    {
        if (bitWidth <= 0 || bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth));

        if (destination.Length < 1024)
            throw new ArgumentException("Destination span must have at least 1024 elements.", nameof(destination));

        int sliceBytes = (16 * bitWidth + 7) >> 3;
        int requiredBytes = checked(64 * sliceBytes);
        if (packed.Length < requiredBytes)
            throw new ArgumentException($"Packed buffer is too short. Required: {requiredBytes}, Actual: {packed.Length}", nameof(packed));

        for (int i = 0; i < 64; i++)
        {
            Unpack16(packed.Slice(i * sliceBytes), destination.Slice(i * 16, 16), bitWidth);
        }
    }
#endif
}
