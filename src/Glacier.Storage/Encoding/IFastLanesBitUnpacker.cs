namespace Glacier.Storage.Encoding;

using System;

/// <summary>
/// FastLanes SIMD bit-unpacking engine interface.
/// Unpacks 16 32-bit integer lanes at peak hardware throughput.
/// </summary>
public interface IFastLanesBitUnpacker
{
    /// <summary>
    /// Unpacks 16 values of specified <paramref name="bitWidth"/> from <paramref name="packed"/> buffer into <paramref name="destination"/> span.
    /// </summary>
    /// <param name="packed">The bit-packed input buffer.</param>
    /// <param name="destination">The destination span receiving at least 16 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    void Unpack16Lanes(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth);

    /// <summary>
    /// Unpacks 16 values of specified <paramref name="bitWidth"/> starting at a specific <paramref name="laneOffset"/>.
    /// </summary>
    /// <param name="packed">The bit-packed input buffer.</param>
    /// <param name="destination">The destination span receiving at least 16 unpacked 32-bit integers.</param>
    /// <param name="bitWidth">The bit-width of each packed value (1 to 32).</param>
    /// <param name="laneOffset">Zero-based lane offset into the packed stream.</param>
    void Unpack16Lanes(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int laneOffset);
}
