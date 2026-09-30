namespace Glacier.Storage.BufferPool;

using System;
using System.Runtime.CompilerServices;
using System.Threading;

public enum PageTier : int
{
    Small = 4096,         // 4 KB small pages
    Medium = 65536,      // 64 KB medium pages
    Huge = 2097152       // 2 MB huge pages
}

/// <summary>
/// Non-blocking atomic frame latch.
/// Latches use atomic 32-bit state:
/// - Bits 0..29: Shared reader count (up to 1,073,741,823 readers)
/// - Bit 30: Exclusive write lock bit
/// - Bit 31: Dirty flag
/// </summary>
public struct FrameLatch
{
    private int _state;

    private const int ReaderMask = 0x3FFFFFFF;
    private const int WriterBit = 1 << 30;
    private const int DirtyBit = unchecked((int)0x80000000);

    public readonly bool IsDirty => (_state & DirtyBit) != 0;
    public readonly bool IsLockedExclusive => (_state & WriterBit) != 0;
    public readonly int ReaderCount => _state & ReaderMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkDirty()
    {
        Interlocked.Or(ref _state, DirtyBit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearDirty()
    {
        Interlocked.And(ref _state, ~DirtyBit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAcquireShared()
    {
        var spin = new SpinWait();
        while (true)
        {
            int current = Volatile.Read(ref _state);
            if ((current & WriterBit) != 0)
            {
                // Write locked
                return false;
            }

            int next = current + 1;
            if (Interlocked.CompareExchange(ref _state, next, current) == current)
            {
                return true;
            }

            spin.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AcquireShared()
    {
        var spin = new SpinWait();
        while (true)
        {
            int current = Volatile.Read(ref _state);
            if ((current & WriterBit) == 0)
            {
                int next = current + 1;
                if (Interlocked.CompareExchange(ref _state, next, current) == current)
                {
                    return;
                }
            }
            spin.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseShared()
    {
        var spin = new SpinWait();
        while (true)
        {
            int current = Volatile.Read(ref _state);
            int readers = current & ReaderMask;
            if (readers <= 0)
            {
                throw new InvalidOperationException("Frame latch reader underflow.");
            }

            int next = (current & ~ReaderMask) | (readers - 1);
            if (Interlocked.CompareExchange(ref _state, next, current) == current)
            {
                return;
            }
            spin.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAcquireExclusive()
    {
        int current = Volatile.Read(ref _state);
        if ((current & (WriterBit | ReaderMask)) != 0)
        {
            return false;
        }

        int next = current | WriterBit;
        return Interlocked.CompareExchange(ref _state, next, current) == current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AcquireExclusive()
    {
        var spin = new SpinWait();
        while (true)
        {
            int current = Volatile.Read(ref _state);
            if ((current & (WriterBit | ReaderMask)) == 0)
            {
                int next = current | WriterBit;
                if (Interlocked.CompareExchange(ref _state, next, current) == current)
                {
                    return;
                }
            }
            spin.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseExclusive()
    {
        var spin = new SpinWait();
        while (true)
        {
            int current = Volatile.Read(ref _state);
            if ((current & WriterBit) == 0)
            {
                throw new InvalidOperationException("Frame latch was not held exclusively.");
            }

            int next = current & ~WriterBit;
            if (Interlocked.CompareExchange(ref _state, next, current) == current)
            {
                return;
            }
            spin.SpinOnce();
        }
    }
}
