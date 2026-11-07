namespace Glacier.Storage.Encoding;

using System;

/// <summary>
/// FastLanes SIMD bit-unpacking engine interface.
/// Unpacks 16 32-bit integer lanes at peak hardware throughput.
/// </summary>
public interface IFastLanesBitUnpacker
{
    /// <summary>
    /// Unpacks 16 values of specified bitWidth from packed buffer into destination span.
    /// </summary>
    void Unpack16Lanes(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth);

    /// <summary>
    /// Unpacks 16 values of specified bitWidth starting at a specific laneOffset.
    /// </summary>
    void Unpack16Lanes(ReadOnlySpan<byte> packed, Span<uint> destination, int bitWidth, int laneOffset);
}
