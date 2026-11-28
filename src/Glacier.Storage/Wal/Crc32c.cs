namespace Glacier.Storage.Wal;

using System;
using System.Runtime.CompilerServices;
#if !USE_LOCAL_FORMATS
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

/// <summary>
/// Hardware-accelerated CRC-32C (Castagnoli, polynomial 0x82F63B78) calculator.
/// Uses SSE4.2 / ARM64 hardware instructions with 8-byte unrolled processing and lookup table fallback.
/// </summary>
public static class Crc32c
{
#if USE_LOCAL_FORMATS
    /// <summary>
    /// Computes the Castagnoli CRC-32C checksum for <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The input byte span.</param>
    /// <returns>The computed 32-bit CRC-32C checksum.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data) =>
        Glacier.Formats.Hashing.Crc32c.Compute(data);

    /// <summary>
    /// Updates an in-progress unfinalized CRC-32C state with additional <paramref name="data"/> bytes.
    /// </summary>
    /// <param name="crc">The running pre-inverted CRC-32C state (initial value <c>0xFFFFFFFF</c>).</param>
    /// <param name="data">The additional input byte span.</param>
    /// <returns>The updated running CRC-32C state.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Update(uint crc, ReadOnlySpan<byte> data) =>
        Glacier.Formats.Hashing.Crc32c.Update(crc, data);
#else
    private static readonly uint[] Table = InitializeTable();

    private static uint[] InitializeTable()
    {
        var table = new uint[256];
        const uint poly = 0x82F63B78;
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Computes the Castagnoli CRC-32C checksum for <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The input byte span.</param>
    /// <returns>The computed 32-bit CRC-32C checksum.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        return Update(0xFFFFFFFF, data) ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Updates an in-progress unfinalized CRC-32C state with additional <paramref name="data"/> bytes.
    /// </summary>
    /// <param name="crc">The running pre-inverted CRC-32C state (initial value <c>0xFFFFFFFF</c>).</param>
    /// <param name="data">The additional input byte span.</param>
    /// <returns>The updated running CRC-32C state.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return crc;

        ref byte ptr = ref MemoryMarshal.GetReference(data);
        int len = data.Length;
        int offset = 0;

        if (Sse42.IsSupported)
        {
            if (Sse42.X64.IsSupported)
            {
                ulong crc64 = crc;
                while (len - offset >= 8)
                {
                    ulong val = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ptr, offset));
                    crc64 = Sse42.X64.Crc32(crc64, val);
                    offset += 8;
                }
                crc = (uint)crc64;
            }
            else
            {
                while (len - offset >= 4)
                {
                    uint val = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ptr, offset));
                    crc = Sse42.Crc32(crc, val);
                    offset += 4;
                }
            }

            while (len - offset > 0)
            {
                byte val = Unsafe.Add(ref ptr, offset);
                crc = Sse42.Crc32(crc, val);
                offset++;
            }
            return crc;
        }

        if (Crc32.Arm64.IsSupported)
        {
            while (len - offset >= 8)
            {
                ulong val = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ptr, offset));
                crc = Crc32.Arm64.ComputeCrc32C(crc, val);
                offset += 8;
            }

            while (len - offset > 0)
            {
                byte val = Unsafe.Add(ref ptr, offset);
                crc = Crc32.ComputeCrc32C(crc, val);
                offset++;
            }
            return crc;
        }

        // Software lookup fallback
        while (offset < len)
        {
            byte val = Unsafe.Add(ref ptr, offset);
            crc = (crc >> 8) ^ Table[(crc & 0xFF) ^ val];
            offset++;
        }
        return crc;
    }
#endif
}
