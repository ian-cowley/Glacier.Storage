namespace Glacier.Storage.BufferPool;

using System;
using System.Runtime.CompilerServices;
using System.Threading;

/// <summary>
/// Replacement-policy classification of a buffer frame.
/// </summary>
public enum PageStatus : byte
{
    /// <summary>Frame is unpopulated.</summary>
    Empty = 0,

    /// <summary>Frame holds a cold (recently loaded or demoted) page.</summary>
    Cold = 1,

    /// <summary>Frame holds a hot (frequently accessed) page.</summary>
    Hot = 2,

    /// <summary>Non-resident test history marker.</summary>
    Test = 3
}

/// <summary>
/// Atomic residency lifecycle state of a <see cref="BufferFrame"/>.
/// </summary>
public enum FrameState : byte
{
    /// <summary>Frame is free and contains no page.</summary>
    Empty = 0,

    /// <summary>Frame is claimed for loading or initializing a new page.</summary>
    Loading = 1,

    /// <summary>Frame holds a valid resident page indexed in the page table.</summary>
    Resident = 2,

    /// <summary>Frame is currently being flushed and evicted from the pool.</summary>
    Evicting = 3
}

/// <summary>
/// Descriptor for a single aligned native-memory frame slot in a <see cref="BufferPool"/>.
/// </summary>
public sealed unsafe class BufferFrame
{
    /// <summary>Zero-based index of this frame inside the owning <see cref="BufferPool"/>.</summary>
    public readonly int FrameIndex;

    /// <summary>Pointer to the 64-byte aligned native memory slab for this frame.</summary>
    public readonly byte* MemoryPtr;

    /// <summary>Size of this frame in bytes.</summary>
    public readonly int PageSize;

    internal readonly object StateLock = new();

    /// <summary>Current logical page stored in this frame.</summary>
    public PageId PageId;

    /// <summary>Atomic reader/writer/dirty latch protecting frame contents.</summary>
    public FrameLatch Latch;

    /// <summary>Number of active pins on this frame.</summary>
    public volatile int PinCount;

    /// <summary>Replacement-policy status of this frame.</summary>
    public volatile PageStatus Status;

    /// <summary>Clock reference bit used by replacement policies.</summary>
    public volatile int RefBit;

    /// <summary>Current atomic lifecycle state of this frame.</summary>
    public volatile FrameState State;

    /// <summary>
    /// Initializes a new <see cref="BufferFrame"/>.
    /// </summary>
    /// <param name="frameIndex">Zero-based frame index.</param>
    /// <param name="memoryPtr">Pointer to aligned page memory.</param>
    /// <param name="pageSize">Page size in bytes.</param>
    public BufferFrame(int frameIndex, byte* memoryPtr, int pageSize)
    {
        FrameIndex = frameIndex;
        MemoryPtr = memoryPtr;
        PageSize = pageSize;
        PageId = default;
        Status = PageStatus.Empty;
        State = FrameState.Empty;
        PinCount = 0;
        RefBit = 0;
    }

    /// <summary>Gets a writable <see cref="Span{T}"/> over the frame's native memory slab.</summary>
    public Span<byte> Span => new(MemoryPtr, PageSize);

    internal bool IsEvictable => PinCount == 0 && (State == FrameState.Empty || State == FrameState.Resident);

    /// <summary>
    /// Atomically increments <see cref="PinCount"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementPin()
    {
        Interlocked.Increment(ref PinCount);
    }

    /// <summary>
    /// Atomically decrements <see cref="PinCount"/> without permitting negative counts.
    /// </summary>
    /// <returns>The updated non-negative pin count.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="PinCount"/> is already zero or negative.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecrementPin()
    {
        var spin = new SpinWait();
        while (true)
        {
            int current = PinCount;
            if (current <= 0)
            {
                throw new InvalidOperationException("Buffer frame pin count cannot be negative.");
            }

            int next = current - 1;
            if (Interlocked.CompareExchange(ref PinCount, next, current) == current)
            {
                return next;
            }
            spin.SpinOnce();
        }
    }
}
