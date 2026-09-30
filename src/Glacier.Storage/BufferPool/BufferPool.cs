namespace Glacier.Storage.BufferPool;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

public sealed unsafe class BufferPool : IBufferPool
{
    private readonly int _poolSize;
    private readonly int _pageSize;
    private readonly byte* _memoryBlock;
    private readonly BufferFrame[] _frames;
    private readonly ConcurrentDictionary<PageId, int> _pageTable;
    private readonly ConcurrentDictionary<PageId, byte[]> _inMemoryStore;
    private readonly IPageReplacementPolicy _policy;
    private readonly Stream? _fileStream;
    private readonly object _ioLock = new();

    private int _nextPageNumber = 1;
    private int _defaultFileId = 1;
    private bool _isDisposed;

    public int PoolSize => _poolSize;
    public int PageSize => _pageSize;

    public BufferPool(int poolSize, PageTier tier = PageTier.Small, IPageReplacementPolicy? policy = null, Stream? fileStream = null)
        : this(poolSize, (int)tier, policy, fileStream)
    {
    }

    public BufferPool(int poolSize, int pageSize, IPageReplacementPolicy? policy = null, Stream? fileStream = null)
    {
        if (poolSize <= 0) throw new ArgumentOutOfRangeException(nameof(poolSize));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

        _poolSize = poolSize;
        _pageSize = pageSize;
        _policy = policy ?? new ClockProPolicy(poolSize);
        _fileStream = fileStream;

        nuint totalBytes = (nuint)(_poolSize * _pageSize);
        _memoryBlock = (byte*)NativeMemory.AlignedAlloc(totalBytes, 64);
        NativeMemory.Clear(_memoryBlock, totalBytes);

        _frames = new BufferFrame[_poolSize];
        for (int i = 0; i < _poolSize; i++)
        {
            byte* framePtr = _memoryBlock + (i * _pageSize);
            _frames[i] = new BufferFrame(i, framePtr, _pageSize);
        }

        _pageTable = new ConcurrentDictionary<PageId, int>();
        _inMemoryStore = new ConcurrentDictionary<PageId, byte[]>();
    }

    public PagePin PinPage(PageId pageId, LockMode mode)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        BufferFrame frame = GetOrLoadFrame(pageId);
        frame.IncrementPin();

        if (mode == LockMode.Shared)
        {
            frame.Latch.AcquireShared();
        }
        else
        {
            frame.Latch.AcquireExclusive();
        }

        _policy.RecordAccess(frame);
        return new PagePin(pageId, frame.Span, this);
    }

    public PageId AllocatePage()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        int pageNum = Interlocked.Increment(ref _nextPageNumber);
        var pageId = new PageId(_defaultFileId, pageNum);

        int frameIdx = EvictAndGetFrame();
        BufferFrame frame = _frames[frameIdx];

        frame.Latch.AcquireExclusive();
        frame.PageId = pageId;
        NativeMemory.Clear(frame.MemoryPtr, (nuint)_pageSize);
        frame.IncrementPin();
        frame.Latch.MarkDirty();

        _pageTable[pageId] = frameIdx;
        _policy.RecordAccess(frame);

        frame.Latch.ReleaseExclusive();
        frame.DecrementPin();

        return pageId;
    }

    public void UnpinPage(PageId pageId, bool isDirty)
    {
        if (!_pageTable.TryGetValue(pageId, out int frameIdx))
        {
            return;
        }

        BufferFrame frame = _frames[frameIdx];
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

    public void MarkDirty(PageId pageId)
    {
        if (_pageTable.TryGetValue(pageId, out int frameIdx))
        {
            _frames[frameIdx].Latch.MarkDirty();
        }
    }

    public void FlushPage(PageId pageId)
    {
        if (_pageTable.TryGetValue(pageId, out int frameIdx))
        {
            BufferFrame frame = _frames[frameIdx];
            FlushFrame(frame);
        }
    }

    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        for (int i = 0; i < _frames.Length; i++)
        {
            BufferFrame frame = _frames[i];
            if (frame.Status != PageStatus.Empty && frame.Latch.IsDirty)
            {
                FlushFrame(frame);
            }
        }
    }

    private BufferFrame GetOrLoadFrame(PageId pageId)
    {
        if (_pageTable.TryGetValue(pageId, out int frameIdx))
        {
            return _frames[frameIdx];
        }

        int victimIdx = EvictAndGetFrame();
        BufferFrame frame = _frames[victimIdx];

        frame.PageId = pageId;
        LoadFrameFromDisk(frame, pageId);
        _pageTable[pageId] = victimIdx;

        return frame;
    }

    private int EvictAndGetFrame()
    {
        int victimIdx = _policy.SelectVictim(_frames);
        if (victimIdx < 0)
        {
            throw new InvalidOperationException("Buffer pool exhaustion: all frames are currently pinned.");
        }

        BufferFrame victim = _frames[victimIdx];
        if (victim.Status != PageStatus.Empty)
        {
            if (victim.Latch.IsDirty)
            {
                FlushFrame(victim);
            }

            _pageTable.TryRemove(victim.PageId, out _);
            _policy.OnEvict(victim);
        }

        return victimIdx;
    }

    private void FlushFrame(BufferFrame frame)
    {
        if (frame.Status != PageStatus.Empty)
        {
            if (_fileStream != null)
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
                // In-memory store backing
                byte[] pageCopy = new byte[_pageSize];
                new ReadOnlySpan<byte>(frame.MemoryPtr, _pageSize).CopyTo(pageCopy);
                _inMemoryStore[frame.PageId] = pageCopy;
            }
        }
        frame.Latch.ClearDirty();
    }

    private void LoadFrameFromDisk(BufferFrame frame, PageId pageId)
    {
        if (_fileStream != null)
        {
            lock (_ioLock)
            {
                long offset = (long)pageId.PageNumber * _pageSize;
                if (offset < _fileStream.Length)
                {
                    _fileStream.Seek(offset, SeekOrigin.Begin);
                    var span = new Span<byte>(frame.MemoryPtr, _pageSize);
                    int read = _fileStream.Read(span);
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
            stored.AsSpan().CopyTo(new Span<byte>(frame.MemoryPtr, _pageSize));
            return;
        }

        NativeMemory.Clear(frame.MemoryPtr, (nuint)_pageSize);
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            Checkpoint();
            _isDisposed = true;
            NativeMemory.AlignedFree(_memoryBlock);
        }
    }
}
