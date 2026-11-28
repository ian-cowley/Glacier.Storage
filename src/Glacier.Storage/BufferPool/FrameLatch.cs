namespace Glacier.Storage.BufferPool;

using System;
using System.Runtime.CompilerServices;
using System.Threading;

/// <summary>
/// Supported buffer pool page sizes.
/// </summary>
public enum PageTier : int
{
    /// <summary>4 KB small pages (supported by <see cref="SlottedPage"/>).</summary>
    Small = 4096,

    /// <summary>64 KB medium pages.</summary>
    Medium = 65536,

    /// <summary>2 MB huge pages.</summary>
    Huge = 2097152
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

    internal const int ReaderMask = 0x3FFFFFFF;
    private const int WriterBit = 1 << 30;
    private const int DirtyBit = unchecked((int)0x80000000);

    internal FrameLatch(int initialState)
    {
        _state = initialState;
    }

    /// <summary>Gets whether the dirty flag bit is set.</summary>
    public readonly bool IsDirty => (Volatile.Read(ref Unsafe.AsRef(in _state)) & DirtyBit) != 0;

    /// <summary>Gets whether the exclusive writer lock bit is set.</summary>
    public readonly bool IsLockedExclusive => (Volatile.Read(ref Unsafe.AsRef(in _state)) & WriterBit) != 0;

    /// <summary>Gets the current number of active shared readers.</summary>
    public readonly int ReaderCount => Volatile.Read(ref Unsafe.AsRef(in _state)) & ReaderMask;

    /// <summary>
    /// Atomically sets the dirty bit on this latch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkDirty()
    {
        Interlocked.Or(ref _state, DirtyBit);
    }

    /// <summary>
    /// Atomically clears the dirty bit on this latch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearDirty()
    {
        Interlocked.And(ref _state, ~DirtyBit);
    }

    /// <summary>
    /// Attempts to acquire a shared reader lock without spinning for an exclusive writer.
    /// </summary>
    /// <returns><c>true</c> if acquired; <c>false</c> if exclusively locked or saturated.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAcquireShared()
    {
        var spin = new SpinWait();
        while (true)
        {
            int current = Volatile.Read(ref _state);
            if ((current & WriterBit) != 0 || (current & ReaderMask) == ReaderMask)
            {
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

    /// <summary>
    /// Acquires a shared reader lock, spinning until any exclusive writer releases.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the shared reader count is saturated.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AcquireShared()
    {
        var spin = new SpinWait();
        while (true)
        {
            int current = Volatile.Read(ref _state);
            if ((current & ReaderMask) == ReaderMask)
            {
                throw new InvalidOperationException("Frame latch shared reader count saturated.");
            }

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

    /// <summary>
    /// Releases one shared reader lock.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if no shared readers are active.</exception>
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

    /// <summary>
    /// Attempts to acquire the exclusive writer lock without waiting.
    /// </summary>
    /// <returns><c>true</c> if acquired; <c>false</c> if readers or another writer hold the latch.</returns>
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

    /// <summary>
    /// Acquires the exclusive writer lock, spinning until all readers and prior writers release.
    /// </summary>
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

    /// <summary>
    /// Releases the exclusive writer lock.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the latch is not held exclusively.</exception>
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
