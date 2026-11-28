namespace Glacier.Storage.BufferPool;

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

/// <summary>
/// Slotted page format for storing variable-length records inside a single buffer frame
/// up to <see cref="ushort.MaxValue"/> bytes (e.g., <see cref="PageTier.Small"/>).
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

    /// <summary>
    /// Initializes a slotted page header in <paramref name="page"/> for <paramref name="pageId"/>.
    /// Supports page sizes from 16 bytes up to 65,535 bytes (such as <see cref="PageTier.Small"/>).
    /// </summary>
    /// <param name="page">The destination page span.</param>
    /// <param name="pageId">The page identifier to record in the header.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="page"/> is smaller than 16 bytes or larger than 65,535 bytes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Initialize(Span<byte> page, PageId pageId)
    {
        ValidatePageBounds(page);

        BinaryPrimitives.WriteInt32LittleEndian(page[..4], pageId.FileId);
        BinaryPrimitives.WriteInt32LittleEndian(page.Slice(4, 4), pageId.PageNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(8, 2), 0); // SlotCount = 0
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(10, 2), (ushort)page.Length); // FreeSpaceOffset at end
        BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(12, 4), 0); // Reserved
    }

    /// <summary>
    /// Reads the slot count from the slotted page header.
    /// </summary>
    /// <param name="page">The slotted page bytes.</param>
    /// <returns>The number of allocated slots.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetSlotCount(ReadOnlySpan<byte> page)
    {
        ValidatePageBounds(page);
        return BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(8, 2));
    }

    /// <summary>
    /// Reads the current downward-growing free-space boundary offset from the slotted page header.
    /// </summary>
    /// <param name="page">The slotted page bytes.</param>
    /// <returns>The byte offset where record payloads begin.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetFreeSpaceOffset(ReadOnlySpan<byte> page)
    {
        ValidatePageBounds(page);
        return BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(10, 2));
    }

    /// <summary>
    /// Computes the remaining contiguous free space between the slot directory and record payloads.
    /// Returns <c>0</c> if the header is corrupt or uninitialized.
    /// </summary>
    /// <param name="page">The slotted page bytes.</param>
    /// <returns>Available bytes, or <c>0</c> if corrupt or full.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AvailableSpace(ReadOnlySpan<byte> page)
    {
        if (!TryReadValidatedHeader(page, out ushort slotCount, out ushort freeOffset))
        {
            return 0;
        }

        int slotEnd = HeaderSize + (slotCount * 4);
        return freeOffset - slotEnd;
    }

    /// <summary>
    /// Attempts to insert a variable-length <paramref name="record"/> into <paramref name="page"/>.
    /// </summary>
    /// <param name="page">The slotted page span.</param>
    /// <param name="record">The record payload bytes.</param>
    /// <param name="slotIndex">Receives the assigned zero-based slot index when successful.</param>
    /// <returns><c>true</c> if inserted; <c>false</c> if insufficient space or the page header is corrupt.</returns>
    public static bool TryInsertRecord(Span<byte> page, ReadOnlySpan<byte> record, out ushort slotIndex)
    {
        slotIndex = 0;
        if (!TryReadValidatedHeader(page, out ushort slotCount, out ushort freeOffset))
        {
            return false;
        }

        if (slotCount == ushort.MaxValue)
        {
            return false;
        }

        int slotEnd = HeaderSize + (slotCount * 4);
        int available = freeOffset - slotEnd;
        long needed = 4L + record.Length; // 4 bytes for new slot entry + record payload
        if (available < needed)
        {
            return false;
        }

        ushort newFreeOffset = (ushort)(freeOffset - record.Length);
        record.CopyTo(page.Slice(newFreeOffset, record.Length));

        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(slotEnd, 2), newFreeOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(slotEnd + 2, 2), (ushort)record.Length);

        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(8, 2), (ushort)(slotCount + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(10, 2), newFreeOffset);

        slotIndex = slotCount;
        return true;
    }

    /// <summary>
    /// Attempts to retrieve the record stored at <paramref name="slotIndex"/>.
    /// </summary>
    /// <param name="page">The slotted page bytes.</param>
    /// <param name="slotIndex">The zero-based slot index.</param>
    /// <param name="record">Receives a read-only span over the record payload.</param>
    /// <returns><c>true</c> if the slot is valid and non-tombstoned; otherwise <c>false</c>.</returns>
    public static bool TryGetRecord(ReadOnlySpan<byte> page, ushort slotIndex, out ReadOnlySpan<byte> record)
    {
        if (!TryReadValidatedHeader(page, out ushort slotCount, out ushort freeOffset) || slotIndex >= slotCount)
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

        int slotEnd = HeaderSize + (slotCount * 4);
        if (offset < freeOffset || offset < slotEnd || (int)offset + length > page.Length)
        {
            record = default;
            return false;
        }

        record = page.Slice(offset, length);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidatePageBounds(ReadOnlySpan<byte> page)
    {
        if (page.Length < HeaderSize || page.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(page),
                $"SlottedPage supports page sizes from {HeaderSize} to {ushort.MaxValue} bytes (got {page.Length}).");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadValidatedHeader(ReadOnlySpan<byte> page, out ushort slotCount, out ushort freeOffset)
    {
        ValidatePageBounds(page);
        slotCount = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(8, 2));
        freeOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(10, 2));

        int slotEnd = HeaderSize + (slotCount * 4);
        return freeOffset <= page.Length && freeOffset >= slotEnd;
    }
}
