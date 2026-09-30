namespace Glacier.Storage.BufferPool;

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

/// <summary>
/// Slotted page format for storing variable-length records inside a single buffer frame.
/// Layout:
/// [Header: 16 bytes]
///   - PageId.FileId: int32 (4B)
///   - PageId.PageNumber: int32 (4B)
///   - SlotCount: uint16 (2B)
///   - FreeSpaceOffset: uint16 (2B) (grows backward from end)
///   - Reserved: uint32 (4B)
/// [Slot Directory]: N * 4 bytes (Offset: uint16, Length: uint16)
/// ... [Free Space] ...
/// [Record Data]: grows downward from end of page.
/// </summary>
public static class SlottedPage
{
    private const int HeaderSize = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Initialize(Span<byte> page, PageId pageId)
    {
        BinaryPrimitives.WriteInt32LittleEndian(page[..4], pageId.FileId);
        BinaryPrimitives.WriteInt32LittleEndian(page.Slice(4, 4), pageId.PageNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(8, 2), 0); // SlotCount = 0
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(10, 2), (ushort)page.Length); // FreeSpaceOffset at end
        BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(12, 4), 0); // Reserved
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetSlotCount(ReadOnlySpan<byte> page) =>
        BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(8, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetFreeSpaceOffset(ReadOnlySpan<byte> page) =>
        BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(10, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AvailableSpace(ReadOnlySpan<byte> page)
    {
        ushort slotCount = GetSlotCount(page);
        ushort freeOffset = GetFreeSpaceOffset(page);
        int slotEnd = HeaderSize + (slotCount * 4);
        return freeOffset - slotEnd;
    }

    public static bool TryInsertRecord(Span<byte> page, ReadOnlySpan<byte> record, out ushort slotIndex)
    {
        slotIndex = 0;
        int needed = 4 + record.Length; // 4 bytes for new slot entry + record payload
        if (AvailableSpace(page) < needed)
        {
            return false;
        }

        ushort slotCount = GetSlotCount(page);
        ushort freeOffset = GetFreeSpaceOffset(page);

        ushort newFreeOffset = (ushort)(freeOffset - record.Length);
        record.CopyTo(page.Slice(newFreeOffset, record.Length));

        int slotDirOffset = HeaderSize + (slotCount * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(slotDirOffset, 2), newFreeOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(slotDirOffset + 2, 2), (ushort)record.Length);

        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(8, 2), (ushort)(slotCount + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(10, 2), newFreeOffset);

        slotIndex = slotCount;
        return true;
    }

    public static bool TryGetRecord(ReadOnlySpan<byte> page, ushort slotIndex, out ReadOnlySpan<byte> record)
    {
        ushort slotCount = GetSlotCount(page);
        if (slotIndex >= slotCount)
        {
            record = default;
            return false;
        }

        int slotDirOffset = HeaderSize + (slotIndex * 4);
        ushort offset = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(slotDirOffset, 2));
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(slotDirOffset + 2, 2));

        if (offset == 0 && length == 0) // Deleted or tombstone
        {
            record = default;
            return false;
        }

        record = page.Slice(offset, length);
        return true;
    }
}
