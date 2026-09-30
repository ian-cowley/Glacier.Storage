namespace Glacier.Storage.Arrow;

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

/// <summary>
/// Pure C# framing and zero-allocation metadata parser for Apache Arrow IPC streaming protocol.
/// </summary>
public static class ArrowIpcParser
{
    public const uint ContinuationMarker = 0xFFFFFFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadPrefix(ReadOnlySpan<byte> source, out int metadataLength)
    {
        metadataLength = 0;
        if (source.Length < 8) return false;

        uint marker = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        if (marker == ContinuationMarker)
        {
            metadataLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4, 4));
        }
        else
        {
            metadataLength = (int)marker;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadMessageHeader(
        ReadOnlySpan<byte> source,
        out int metadataLength,
        out ArrowMessageType messageType,
        out long bodyLength,
        out int headerBytesConsumed)
    {
        messageType = ArrowMessageType.None;
        bodyLength = 0;
        headerBytesConsumed = 0;

        if (!TryReadPrefix(source, out metadataLength))
        {
            return false;
        }

        if (metadataLength == 0)
        {
            headerBytesConsumed = 8;
            return true;
        }

        if (source.Length < 8 + metadataLength) return false;

        ReadOnlySpan<byte> metaSpan = source.Slice(8, metadataLength);
        if (metaSpan.Length >= 16)
        {
            messageType = (ArrowMessageType)metaSpan[0];
            bodyLength = BinaryPrimitives.ReadInt64LittleEndian(metaSpan.Slice(4, 8));
        }

        int alignedMeta = (metadataLength + 7) & ~7;
        headerBytesConsumed = 8 + alignedMeta;
        return true;
    }
}
