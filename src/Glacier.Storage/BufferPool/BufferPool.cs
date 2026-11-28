namespace Glacier.Storage.BufferPool;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Thread-safe, fixed-capacity unmanaged buffer pool with 64-byte aligned native slabs,
/// atomic frame lifecycle states, lock-free/random-access page I/O, and adaptive replacement.
/// </summary>
public sealed unsafe class BufferPool : IBufferPool
{
    private sealed class InflightSignal
    {
        private readonly ManualResetEventSlim _mre = new(false);

        public void Complete() => _mre.Set();
        public void Wait() => _mre.Wait();
    }

    private readonly int _poolSize;
    private readonly int _pageSize;
    private byte* _memoryBlock;
    private readonly BufferFrame[] _frames;
    private readonly ConcurrentDictionary<PageId, int> _pageTable;
    private readonly ConcurrentDictionary<PageId, byte[]> _inMemoryStore;
    private readonly IPageReplacementPolicy _policy;
    private readonly Stream? _fileStream;
    private readonly object _ioLock = new();
    private readonly object _missLock = new();
    private readonly Dictionary<PageId, InflightSignal> _inflightLoads = new();
    private readonly ConcurrentDictionary<PageId, InflightSignal> _inflightEvictions = new();

    private int _nextPageNumber = 0;
    private readonly int _defaultFileId = 1;
    private int _disposed;

    /// <summary>Gets the maximum number of resident frames in this pool.</summary>
    public int PoolSize => _poolSize;

    /// <summary>Gets the size of each page frame in bytes.</summary>
    public int PageSize => _pageSize;

    /// <summary>
    /// Initializes a new <see cref="BufferPool"/> with a standard <see cref="PageTier"/> size.
    /// </summary>
    /// <param name="poolSize">Number of page frames to allocate.</param>
    /// <param name="tier">Predefined page size tier.</param>
    /// <param name="policy">Optional replacement policy (defaults to <see cref="ClockProPolicy"/>).</param>
    /// <param name="fileStream">Optional backing stream for page persistence (defaults to in-memory pooled backing).</param>
    public BufferPool(int poolSize, PageTier tier = PageTier.Small, IPageReplacementPolicy? policy = null, Stream? fileStream = null)
        : this(poolSize, (int)tier, policy, fileStream)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="BufferPool"/> with an explicit byte <paramref name="pageSize"/>.
    /// </summary>
    /// <param name="poolSize">Number of page frames to allocate.</param>
    /// <param name="pageSize">Size of each page in bytes.</param>
    /// <param name="policy">Optional replacement policy (defaults to <see cref="ClockProPolicy"/>).</param>
    /// <param name="fileStream">Optional backing stream for page persistence (defaults to in-memory pooled backing).</param>
    public BufferPool(int poolSize, int pageSize, IPageReplacementPolicy? policy = null, Stream? fileStream = null)
    {
        if (poolSize <= 0) throw new ArgumentOutOfRangeException(nameof(poolSize));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

        _poolSize = poolSize;
        _pageSize = pageSize;
        _policy = policy ?? new ClockProPolicy(poolSize);
        _fileStream = fileStream;

        long totalBytesSigned = checked((long)_poolSize * _pageSize);
        nuint totalBytes = checked((nuint)totalBytesSigned);
        _memoryBlock = (byte*)NativeMemory.AlignedAlloc(totalBytes, 64);

        try
        {
            NativeMemory.Clear(_memoryBlock, totalBytes);

            _frames = new BufferFrame[_poolSize];
            for (int i = 0; i < _poolSize; i++)
            {
                byte* framePtr = _memoryBlock + ((long)i * _pageSize);
                _frames[i] = new BufferFrame(i, framePtr, _pageSize);
            }

            _pageTable = new ConcurrentDictionary<PageId, int>();
            _inMemoryStore = new ConcurrentDictionary<PageId, byte[]>();
        }
        catch
        {
            NativeMemory.AlignedFree(_memoryBlock);
            _memoryBlock = null;
            throw;
        }
    }

    /// <summary>
    /// Finalizer ensuring native slab memory is released if <see cref="Dispose()"/> was not called.
    /// </summary>
    ~BufferPool()
    {
        Dispose(disposing: false);
    }

    /// <inheritdoc />
    public PagePin PinPage(PageId pageId, LockMode mode)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (pageId.PageNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageId), "PageNumber cannot be negative.");
        }

        BufferFrame frame;
        bool wasCacheHit = false;

        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_inflightEvictions.TryGetValue(pageId, out var evictSig))
            {
                evictSig.Wait();
                continue;
            }

            if (_pageTable.TryGetValue(pageId, out int hitIdx))
            {
                BufferFrame candidate = _frames[hitIdx];
                lock (candidate.StateLock)
                {
                    if (candidate.State == FrameState.Resident && candidate.PageId == pageId)
                    {
                        candidate.IncrementPin();
                        frame = candidate;
                        wasCacheHit = true;
                        break;
                    }
                }
            }

            InflightSignal? waitSignal = null;
            InflightSignal? myLoad = null;

            lock (_missLock)
            {
                if (_pageTable.TryGetValue(pageId, out int recheckIdx))
                {
                    BufferFrame candidate = _frames[recheckIdx];
                    lock (candidate.StateLock)
                    {
                        if (candidate.State == FrameState.Resident && candidate.PageId == pageId)
                        {
                            candidate.IncrementPin();
                            frame = candidate;
                            wasCacheHit = true;
                            goto AcquireLatch;
                        }
                    }
                }

                if (_inflightEvictions.TryGetValue(pageId, out var evictDuringMiss))
                {
                    waitSignal = evictDuringMiss;
                }
                else if (_inflightLoads.TryGetValue(pageId, out var existingLoad))
                {
                    waitSignal = existingLoad;
                }
                else
                {
                    myLoad = new InflightSignal();
                    _inflightLoads[pageId] = myLoad;
                }
            }

            if (waitSignal != null)
            {
                waitSignal.Wait();
                continue;
            }

            try
            {
                if (_inflightEvictions.TryGetValue(pageId, out var lateEvict))
                {
                    lateEvict.Wait();
                }

                BufferFrame claimed = ClaimVictimFrame(pageId);
                bool published = false;
                try
                {
                    LoadFrameFromDisk(claimed, pageId);
                    _policy.RecordAccess(claimed);

                    lock (claimed.StateLock)
                    {
                        claimed.State = FrameState.Resident;
                    }

                    _pageTable[pageId] = claimed.FrameIndex;
                    published = true;
                }
                finally
                {
                    if (!published)
                    {
                        lock (claimed.StateLock)
                        {
                            claimed.PageId = default;
                            claimed.Latch.ClearDirty();
                            claimed.Status = PageStatus.Empty;
                            claimed.RefBit = 0;
                            claimed.State = FrameState.Empty;
                            claimed.DecrementPin();
                        }
                    }
                }

                frame = claimed;
                break;
            }
            finally
            {
                lock (_missLock)
                {
                    _inflightLoads.Remove(pageId);
                }
                myLoad!.Complete();
            }
        }

    AcquireLatch:
        bool latchAcquired = false;
        try
        {
            if (mode == LockMode.Shared)
            {
                frame.Latch.AcquireShared();
            }
            else
            {
                frame.Latch.AcquireExclusive();
            }
            latchAcquired = true;
        }
        finally
        {
            if (!latchAcquired)
            {
                lock (frame.StateLock)
                {
                    frame.DecrementPin();
                }
            }
        }

        if (wasCacheHit)
        {
            _policy.RecordAccess(frame);
        }

        return new PagePin(pageId, frame.Span, this, mode);
    }

    /// <inheritdoc />
    public PageId AllocatePage()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        int pageNum = Interlocked.Increment(ref _nextPageNumber);
        var pageId = new PageId(_defaultFileId, pageNum);

        BufferFrame frame = ClaimVictimFrame(pageId);
        bool published = false;
        try
        {
            frame.Latch.AcquireExclusive();
            try
            {
                NativeMemory.Clear(frame.MemoryPtr, (nuint)_pageSize);
                frame.Latch.MarkDirty();
                _policy.RecordAccess(frame);

                lock (frame.StateLock)
                {
                    frame.State = FrameState.Resident;
                }
                _pageTable[pageId] = frame.FrameIndex;
                published = true;
            }
            finally
            {
                frame.Latch.ReleaseExclusive();
            }

            return pageId;
        }
        finally
        {
            lock (frame.StateLock)
            {
                if (!published)
                {
                    frame.PageId = default;
                    frame.Latch.ClearDirty();
                    frame.Status = PageStatus.Empty;
                    frame.RefBit = 0;
                    frame.State = FrameState.Empty;
                }
                frame.DecrementPin();
            }
        }
    }

    /// <inheritdoc />
    public void UnpinPage(PageId pageId, bool isDirty)
    {
        if (!_pageTable.TryGetValue(pageId, out int frameIdx))
        {
            return;
        }

        BufferFrame frame = _frames[frameIdx];
        lock (frame.StateLock)
        {
            if (frame.State != FrameState.Resident || frame.PageId != pageId)
            {
                return;
            }

            if (frame.PinCount <= 0)
            {
                throw new InvalidOperationException("Buffer frame pin count cannot be negative.");
            }

            if (isDirty)
            {
                frame.Latch.MarkDirty();
            }

            if (frame.Latch.IsLockedExclusive)
            {
                frame.Latch.ReleaseExclusive();
            }
            else if (frame.Latch.ReaderCount > 0)
            {
                frame.Latch.ReleaseShared();
            }

            frame.DecrementPin();
        }
    }

    /// <inheritdoc />
    public void UnpinPage(PageId pageId, LockMode mode, bool isDirty)
    {
        if (!_pageTable.TryGetValue(pageId, out int frameIdx))
        {
            return;
        }

        BufferFrame frame = _frames[frameIdx];
        lock (frame.StateLock)
        {
            if (frame.State != FrameState.Resident || frame.PageId != pageId)
            {
                return;
            }

            if (frame.PinCount <= 0)
            {
                throw new InvalidOperationException("Buffer frame pin count cannot be negative.");
            }

            if (isDirty)
            {
                frame.Latch.MarkDirty();
            }

            if (mode == LockMode.Exclusive)
            {
                frame.Latch.ReleaseExclusive();
            }
            else
            {
                frame.Latch.ReleaseShared();
            }

            frame.DecrementPin();
        }
    }

    /// <inheritdoc />
    public void MarkDirty(PageId pageId)
    {
        if (_pageTable.TryGetValue(pageId, out int frameIdx))
        {
            BufferFrame frame = _frames[frameIdx];
            lock (frame.StateLock)
            {
                if (frame.State == FrameState.Resident && frame.PageId == pageId)
                {
                    frame.Latch.MarkDirty();
                }
            }
        }
    }

    /// <inheritdoc />
    public void FlushPage(PageId pageId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_pageTable.TryGetValue(pageId, out int frameIdx))
        {
            BufferFrame frame = _frames[frameIdx];
            lock (frame.StateLock)
            {
                if (frame.State != FrameState.Resident || frame.PageId != pageId)
                {
                    return;
                }
                frame.IncrementPin();
            }

            try
            {
                frame.Latch.AcquireShared();
                try
                {
                    FlushFrameCore(frame);
                }
                finally
                {
                    frame.Latch.ReleaseShared();
                }
            }
            finally
            {
                lock (frame.StateLock)
                {
                    frame.DecrementPin();
                }
            }
        }
    }

    /// <inheritdoc />
    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CheckpointCore(skipExclusiveLocked: false);
    }

    private void CheckpointCore(bool skipExclusiveLocked)
    {
        for (int i = 0; i < _frames.Length; i++)
        {
            BufferFrame frame = _frames[i];
            lock (frame.StateLock)
            {
                if (frame.State != FrameState.Resident || !frame.Latch.IsDirty)
                {
                    continue;
                }
                if (skipExclusiveLocked && frame.Latch.IsLockedExclusive)
                {
                    continue;
                }
                frame.IncrementPin();
            }

            try
            {
                if (skipExclusiveLocked)
                {
                    if (!frame.Latch.TryAcquireShared())
                    {
                        continue;
                    }
                }
                else
                {
                    frame.Latch.AcquireShared();
                }

                try
                {
                    FlushFrameCore(frame);
                }
                finally
                {
                    frame.Latch.ReleaseShared();
                }
            }
            finally
            {
                lock (frame.StateLock)
                {
                    frame.DecrementPin();
                }
            }
        }
    }

    private BufferFrame ClaimVictimFrame(PageId targetPageId)
    {
        int maxAttempts = Math.Max(4, _poolSize * 2);
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int victimIdx = _policy.SelectVictim(_frames);
            if (victimIdx < 0)
            {
                throw new InvalidOperationException("Buffer pool exhaustion: all frames are currently pinned.");
            }

            BufferFrame victim = _frames[victimIdx];
            PageId oldPageId;
            bool wasResident = false;
            bool needsFlush = false;
            InflightSignal? evictSignal = null;

            lock (victim.StateLock)
            {
                if (!victim.IsEvictable)
                {
                    continue;
                }

                if (victim.State == FrameState.Empty)
                {
                    victim.PageId = targetPageId;
                    victim.State = FrameState.Loading;
                    victim.IncrementPin();
                    return victim;
                }

                oldPageId = victim.PageId;
                needsFlush = victim.Latch.IsDirty;
                victim.State = FrameState.Evicting;
                victim.IncrementPin();
                evictSignal = new InflightSignal();
                _inflightEvictions[oldPageId] = evictSignal;
                wasResident = true;
            }

            if (wasResident)
            {
                _pageTable.TryRemove(new KeyValuePair<PageId, int>(oldPageId, victimIdx));

                try
                {
                    if (needsFlush)
                    {
                        FlushFrameCore(victim);
                    }

                    _policy.OnEvict(victim);

                    lock (victim.StateLock)
                    {
                        victim.PageId = targetPageId;
                        victim.Latch.ClearDirty();
                        victim.State = FrameState.Loading;
                    }

                    return victim;
                }
                catch
                {
                    lock (victim.StateLock)
                    {
                        victim.PageId = default;
                        victim.Latch.ClearDirty();
                        victim.Status = PageStatus.Empty;
                        victim.RefBit = 0;
                        victim.State = FrameState.Empty;
                        victim.DecrementPin();
                    }
                    throw;
                }
                finally
                {
                    _inflightEvictions.TryRemove(new KeyValuePair<PageId, InflightSignal>(oldPageId, evictSignal!));
                    evictSignal!.Complete();
                }
            }
        }

        throw new InvalidOperationException("Buffer pool exhaustion: all frames are currently pinned.");
    }

    private void FlushFrameCore(BufferFrame frame)
    {
        if (frame.State != FrameState.Empty && frame.Status != PageStatus.Empty)
        {
            if (_fileStream is FileStream fs)
            {
                long offset = (long)frame.PageId.PageNumber * _pageSize;
                var span = new ReadOnlySpan<byte>(frame.MemoryPtr, _pageSize);
                RandomAccess.Write(fs.SafeFileHandle, span, offset);
                RandomAccess.FlushToDisk(fs.SafeFileHandle);
            }
            else if (_fileStream != null)
            {
                lock (_ioLock)
                {
                    long offset = (long)frame.PageId.PageNumber * _pageSize;
                    if (_fileStream.Position != offset)
                    {
                        _fileStream.Seek(offset, SeekOrigin.Begin);
                    }
                    var span = new ReadOnlySpan<byte>(frame.MemoryPtr, _pageSize);
                    _fileStream.Write(span);
                    _fileStream.Flush();
                }
            }
            else
            {
                byte[] buf = _inMemoryStore.GetOrAdd(frame.PageId, _ => ArrayPool<byte>.Shared.Rent(_pageSize));
                new ReadOnlySpan<byte>(frame.MemoryPtr, _pageSize).CopyTo(buf.AsSpan(0, _pageSize));
            }
        }
        frame.Latch.ClearDirty();
    }

    private void LoadFrameFromDisk(BufferFrame frame, PageId pageId)
    {
        if (_fileStream is FileStream fs)
        {
            long offset = (long)pageId.PageNumber * _pageSize;
            long fileLen = RandomAccess.GetLength(fs.SafeFileHandle);
            if (offset < fileLen)
            {
                var span = new Span<byte>(frame.MemoryPtr, _pageSize);
                int totalRead = 0;
                while (totalRead < _pageSize)
                {
                    int read = RandomAccess.Read(fs.SafeFileHandle, span.Slice(totalRead), offset + totalRead);
                    if (read == 0)
                    {
                        break;
                    }
                    totalRead += read;
                }
                if (totalRead < _pageSize)
                {
                    span.Slice(totalRead).Clear();
                }
                return;
            }
        }
        else if (_fileStream != null)
        {
            lock (_ioLock)
            {
                long offset = (long)pageId.PageNumber * _pageSize;
                if (offset < _fileStream.Length)
                {
                    _fileStream.Seek(offset, SeekOrigin.Begin);
                    var span = new Span<byte>(frame.MemoryPtr, _pageSize);
                    int read = _fileStream.ReadAtLeast(span, _pageSize, throwOnEndOfStream: false);
                    if (read < _pageSize)
                    {
                        span.Slice(read).Clear();
                    }
                    return;
                }
            }
        }
        else if (_inMemoryStore.TryGetValue(pageId, out byte[]? stored))
        {
            stored.AsSpan(0, _pageSize).CopyTo(new Span<byte>(frame.MemoryPtr, _pageSize));
            return;
        }

        NativeMemory.Clear(frame.MemoryPtr, (nuint)_pageSize);
    }

    /// <summary>
    /// Flushes dirty resident pages, returns pooled in-memory page buffers, and frees aligned native memory.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (disposing)
            {
                CheckpointCore(skipExclusiveLocked: true);
                foreach (var kvp in _inMemoryStore)
                {
                    ArrayPool<byte>.Shared.Return(kvp.Value);
                }
                _inMemoryStore.Clear();
            }
        }
        finally
        {
            if (_memoryBlock != null)
            {
                NativeMemory.AlignedFree(_memoryBlock);
                _memoryBlock = null;
            }
        }
    }
}
