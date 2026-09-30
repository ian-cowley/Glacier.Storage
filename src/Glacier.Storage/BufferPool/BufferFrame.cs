namespace Glacier.Storage.BufferPool;

using System;
using System.Runtime.CompilerServices;
using System.Threading;

public enum PageStatus : byte
{
    Empty = 0,
    Cold = 1,
    Hot = 2,
    Test = 3
}

public sealed unsafe class BufferFrame
{
    public readonly int FrameIndex;
    public readonly byte* MemoryPtr;
    public readonly int PageSize;

    public PageId PageId;
    public FrameLatch Latch;
    public volatile int PinCount;
    public volatile PageStatus Status;
    public volatile int RefBit;

    public BufferFrame(int frameIndex, byte* memoryPtr, int pageSize)
    {
        FrameIndex = frameIndex;
        MemoryPtr = memoryPtr;
        PageSize = pageSize;
        PageId = default;
        Status = PageStatus.Empty;
        PinCount = 0;
        RefBit = 0;
    }

    public Span<byte> Span => new(MemoryPtr, PageSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementPin()
    {
        Interlocked.Increment(ref PinCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecrementPin()
    {
        return Interlocked.Decrement(ref PinCount);
    }
}
