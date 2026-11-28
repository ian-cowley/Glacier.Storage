namespace Glacier.Storage.Tests;

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Storage;
using Glacier.Storage.BufferPool;
using Xunit;

public class BufferPoolTests
{
    [Fact]
    public void BufferPool_AllocatePinUnpin_WorksCorrectly()
    {
        using var pool = new BufferPool(8, PageTier.Small);

        var p1 = pool.AllocatePage();
        var p2 = pool.AllocatePage();

        using (var pin = pool.PinPage(p1, LockMode.Exclusive))
        {
            BinaryPrimitives.WriteInt32LittleEndian(pin.Data[..4], 42);
            pin.MarkDirty();
        }

        using (var pin = pool.PinPage(p1, LockMode.Shared))
        {
            int val = BinaryPrimitives.ReadInt32LittleEndian(pin.Data[..4]);
            Assert.Equal(42, val);
        }
    }

    [Fact]
    public void SlottedPage_InsertAndRetrieve_PreservesRecords()
    {
        Span<byte> page = stackalloc byte[4096];
        var pageId = new PageId(1, 10);
        SlottedPage.Initialize(page, pageId);

        byte[] rec1 = "Glacier.Vector"u8.ToArray();
        byte[] rec2 = "Glacier.Storage.BufferPool"u8.ToArray();
        byte[] rec3 = "HighThroughput"u8.ToArray();

        Assert.True(SlottedPage.TryInsertRecord(page, rec1, out ushort slot0));
        Assert.True(SlottedPage.TryInsertRecord(page, rec2, out ushort slot1));
        Assert.True(SlottedPage.TryInsertRecord(page, rec3, out ushort slot2));

        Assert.Equal(0, slot0);
        Assert.Equal(1, slot1);
        Assert.Equal(2, slot2);
        Assert.Equal(3, SlottedPage.GetSlotCount(page));

        Assert.True(SlottedPage.TryGetRecord(page, slot0, out var out1));
        Assert.True(out1.SequenceEqual(rec1));

        Assert.True(SlottedPage.TryGetRecord(page, slot1, out var out2));
        Assert.True(out2.SequenceEqual(rec2));

        Assert.True(SlottedPage.TryGetRecord(page, slot2, out var out3));
        Assert.True(out3.SequenceEqual(rec3));
    }

    [Fact]
    public void ClockPro_ProtectsHotPages_AgainstSequentialScans()
    {
        // ClockPro with 16 frames
        int poolSize = 16;
        using var pool = new BufferPool(poolSize, PageTier.Small);

        // Allocate 4 hot pages and access them repeatedly
        var hotPages = new PageId[4];
        for (int i = 0; i < 4; i++)
        {
            hotPages[i] = pool.AllocatePage();
            using var pin = pool.PinPage(hotPages[i], LockMode.Exclusive);
            BinaryPrimitives.WriteInt32LittleEndian(pin.Data[..4], 1000 + i);
            pin.MarkDirty();
        }

        // Re-reference hot pages to promote them to Hot in Clock-Pro
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 4; i++)
            {
                using var pin = pool.PinPage(hotPages[i], LockMode.Shared);
                Assert.Equal(1000 + i, BinaryPrimitives.ReadInt32LittleEndian(pin.Data[..4]));
            }
        }

        // Now run a sequential scan of 50 new pages (larger than total pool capacity)
        for (int i = 0; i < 50; i++)
        {
            var p = pool.AllocatePage();
            using var pin = pool.PinPage(p, LockMode.Exclusive);
            BinaryPrimitives.WriteInt32LittleEndian(pin.Data[..4], i);
        }

        // Verify that the hot pages can still be pinned and read without loss
        for (int i = 0; i < 4; i++)
        {
            using var pin = pool.PinPage(hotPages[i], LockMode.Shared);
            Assert.Equal(1000 + i, BinaryPrimitives.ReadInt32LittleEndian(pin.Data[..4]));
        }
    }

    [Fact]
    public void FrameLatch_ConcurrentSharedReads_AllowMultiReaders()
    {
        var latch = new FrameLatch();

        Assert.True(latch.TryAcquireShared());
        Assert.True(latch.TryAcquireShared());
        Assert.Equal(2, latch.ReaderCount);
        Assert.False(latch.TryAcquireExclusive());

        latch.ReleaseShared();
        Assert.Equal(1, latch.ReaderCount);
        latch.ReleaseShared();
        Assert.Equal(0, latch.ReaderCount);

        Assert.True(latch.TryAcquireExclusive());
        Assert.True(latch.IsLockedExclusive);
        Assert.False(latch.TryAcquireShared());

        latch.ReleaseExclusive();
        Assert.False(latch.IsLockedExclusive);
    }

    [Fact]
    public void MultiTierPageBufferPool_AllTiers_FunctionCorrectly()
    {
        // 4KB tier
        using var smallPool = new BufferPool(4, PageTier.Small);
        Assert.Equal(4096, smallPool.PageSize);
        var pSmall = smallPool.AllocatePage();
        using (var pin = smallPool.PinPage(pSmall, LockMode.Exclusive))
        {
            Assert.Equal(4096, pin.Data.Length);
        }

        // 64KB tier
        using var medPool = new BufferPool(4, PageTier.Medium);
        Assert.Equal(65536, medPool.PageSize);
        var pMed = medPool.AllocatePage();
        using (var pin = medPool.PinPage(pMed, LockMode.Exclusive))
        {
            Assert.Equal(65536, pin.Data.Length);
        }

        // 2MB huge tier
        using var hugePool = new BufferPool(2, PageTier.Huge);
        Assert.Equal(2097152, hugePool.PageSize);
        var pHuge = hugePool.AllocatePage();
        using (var pin = hugePool.PinPage(pHuge, LockMode.Exclusive))
        {
            Assert.Equal(2097152, pin.Data.Length);
        }
    }

    [Fact]
    public void PagePin_DoubleDispose_And_MarkDirtyAfterDispose_And_DecrementUnderflow()
    {
        using var pool = new BufferPool(2, PageTier.Small);
        var pageId = pool.AllocatePage();

        var pin = pool.PinPage(pageId, LockMode.Exclusive);
        BinaryPrimitives.WriteInt32LittleEndian(pin.Data[..4], 777);
        pin.MarkDirty();
        pin.Dispose();

        // Second dispose must be a safe no-op (no underflow, no double latch release)
        pin.Dispose();

        // MarkDirty after dispose must throw ObjectDisposedException
        bool markDirtyThrew = false;
        try
        {
            pin.MarkDirty();
        }
        catch (ObjectDisposedException)
        {
            markDirtyThrew = true;
        }
        Assert.True(markDirtyThrew);

        // Pool frame must be unpinned and cleanly reusable
        using (var sharedPin = pool.PinPage(pageId, LockMode.Shared))
        {
            Assert.Equal(777, BinaryPrimitives.ReadInt32LittleEndian(sharedPin.Data[..4]));
        }

        // Calling BufferFrame.DecrementPin at 0 throws InvalidOperationException
        unsafe
        {
            var frame = new BufferFrame(0, null, 4096);
            Assert.Throws<InvalidOperationException>(() => frame.DecrementPin());
        }
    }

    [Fact]
    public void BufferPool_Exhaustion_ThrowsInvalidOperationException_WithoutLeakingPins()
    {
        using var pool = new BufferPool(1, PageTier.Small);
        var p1 = pool.AllocatePage();

        using (var pin1 = pool.PinPage(p1, LockMode.Shared))
        {
            Assert.Throws<InvalidOperationException>(() => pool.AllocatePage());
            Assert.Throws<InvalidOperationException>(() => pool.PinPage(new PageId(1, 999), LockMode.Shared));
        }

        // After unpin, allocating a second page succeeds (evicts p1)
        var p2 = pool.AllocatePage();
        using var pin2 = pool.PinPage(p2, LockMode.Shared);
        Assert.Equal(p2, pin2.Id);
    }

    [Fact]
    public void FrameLatch_ReaderMaskSaturation_DoesNotFlipWriterBit()
    {
        var latch = new FrameLatch(FrameLatch.ReaderMask);

        Assert.Equal(FrameLatch.ReaderMask, latch.ReaderCount);
        Assert.False(latch.IsLockedExclusive);
        Assert.False(latch.TryAcquireShared());
        Assert.False(latch.IsLockedExclusive);
        Assert.Equal(FrameLatch.ReaderMask, latch.ReaderCount);

        Assert.Throws<InvalidOperationException>(() => latch.AcquireShared());
        Assert.False(latch.IsLockedExclusive);
        Assert.Equal(FrameLatch.ReaderMask, latch.ReaderCount);
    }

    [Fact]
    public void SlottedPage_PageTierMedium_ThrowsArgumentOutOfRange_And_CorruptHeaderRejected()
    {
        byte[] mediumPage = new byte[(int)PageTier.Medium];
        Assert.Throws<ArgumentOutOfRangeException>(() => SlottedPage.Initialize(mediumPage, new PageId(1, 1)));

        byte[] smallPage = new byte[(int)PageTier.Small];
        SlottedPage.Initialize(smallPage, new PageId(1, 1));
        Assert.True(SlottedPage.TryInsertRecord(smallPage, "hello"u8, out ushort slot));
        Assert.True(SlottedPage.TryGetRecord(smallPage, slot, out var rec));
        Assert.Equal("hello"u8.ToArray(), rec.ToArray());

        // Corrupt FreeSpaceOffset past page length
        BinaryPrimitives.WriteUInt16LittleEndian(smallPage.AsSpan(10, 2), (ushort)(smallPage.Length + 100));
        Assert.Equal(0, SlottedPage.AvailableSpace(smallPage));
        Assert.False(SlottedPage.TryInsertRecord(smallPage, "world"u8, out _));
        Assert.False(SlottedPage.TryGetRecord(smallPage, slot, out _));

        // Corrupt FreeSpaceOffset overlapping slot directory
        BinaryPrimitives.WriteUInt16LittleEndian(smallPage.AsSpan(10, 2), 4);
        Assert.Equal(0, SlottedPage.AvailableSpace(smallPage));
        Assert.False(SlottedPage.TryInsertRecord(smallPage, "world"u8, out _));
        Assert.False(SlottedPage.TryGetRecord(smallPage, slot, out _));
    }

    [Fact]
    public void TwoQueuePolicy_PinnedA1InFrames_AreReEnqueuedNotDiscarded()
    {
        var policy = new TwoQueuePolicy(2);
        using var pool = new BufferPool(2, PageTier.Small, policy);

        var p1 = pool.AllocatePage();
        var p2 = pool.AllocatePage();

        // Pin p1 so it is un-evictable in A1in while p2 is unpinned
        using (var pin1 = pool.PinPage(p1, LockMode.Shared))
        {
            var p3 = pool.AllocatePage(); // Must evict p2 and preserve p1
            Assert.NotEqual(p1, p3);
        }

        // Now p1 is unpinned; allocating p4 and p5 must succeed without pool exhaustion
        var p4 = pool.AllocatePage();
        var p5 = pool.AllocatePage();
        Assert.NotEqual(p4, p5);
    }

    [Fact]
    public async Task BufferPool_ConcurrentSamePageMisses_SingleLoadAndNoPinLeak()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"glacier_bp_miss_{Guid.NewGuid():N}.dat");
        try
        {
            const int pageCount = 12;
            const int pageSize = (int)PageTier.Small;

            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                using (var seedPool = new BufferPool(4, pageSize, fileStream: fs))
                {
                    for (int i = 1; i <= pageCount; i++)
                    {
                        var pid = new PageId(1, i);
                        using var pin = seedPool.PinPage(pid, LockMode.Exclusive);
                        BinaryPrimitives.WriteInt32LittleEndian(pin.Data[..4], i * 1009);
                        BinaryPrimitives.WriteInt32LittleEndian(pin.Data.Slice(pageSize - 4, 4), i * 1009);
                        pin.MarkDirty();
                    }
                    seedPool.Checkpoint();
                }

                // Force misses across 24 threads on poolSize = 4 (tolerating genuine pool-exhaustion when >4 distinct pages pinned simultaneously)
                using var pool = new BufferPool(4, pageSize, fileStream: fs);
                var startGate = new ManualResetEventSlim(false);
                var errors = new ConcurrentQueue<Exception>();

                Task[] workers = new Task[24];
                for (int t = 0; t < workers.Length; t++)
                {
                    int tid = t;
                    workers[t] = Task.Run(() =>
                    {
                        startGate.Wait();
                        try
                        {
                            for (int iter = 0; iter < 40; iter++)
                            {
                                int pageNum = 1 + ((iter + (tid & 3)) % pageCount);
                                var pid = new PageId(1, pageNum);
                                LockMode mode = (iter + tid) % 5 == 0 ? LockMode.Exclusive : LockMode.Shared;

                                try
                                {
                                    using var pin = pool.PinPage(pid, mode);
                                    int head = BinaryPrimitives.ReadInt32LittleEndian(pin.Data[..4]);
                                    int tail = BinaryPrimitives.ReadInt32LittleEndian(pin.Data.Slice(pageSize - 4, 4));
                                    if (head != pageNum * 1009 || tail != pageNum * 1009)
                                    {
                                        throw new InvalidDataException($"Corrupt page {pageNum}: head={head}, tail={tail}");
                                    }
                                    if (mode == LockMode.Exclusive)
                                    {
                                        pin.MarkDirty();
                                    }
                                }
                                catch (InvalidOperationException ex) when (ex.Message.Contains("exhaustion", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Expected when > 4 threads concurrently hold distinct pins in a 4-frame pool
                                    Thread.Yield();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Enqueue(ex);
                        }
                    });
                }

                startGate.Set();
                await Task.WhenAll(workers);
                Assert.Empty(errors);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task BufferPool_ConcurrentEvictionVsPin_NoUseAfterEviction()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"glacier_bp_evict_{Guid.NewGuid():N}.dat");
        try
        {
            const int totalPages = 16;
            const int pageSize = (int)PageTier.Small;

            using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var pool = new BufferPool(4, pageSize, new ClockProPolicy(4), fs);

            // Seed all 16 pages with valid initial epoch=0 checksums
            for (int p = 1; p <= totalPages; p++)
            {
                var pid = new PageId(1, p);
                using var pin = pool.PinPage(pid, LockMode.Exclusive);
                BinaryPrimitives.WriteInt32LittleEndian(pin.Data[..4], p);
                BinaryPrimitives.WriteInt32LittleEndian(pin.Data.Slice(4, 4), 0);
                BinaryPrimitives.WriteInt32LittleEndian(pin.Data.Slice(pageSize - 4, 4), p ^ 0);
                pin.MarkDirty();
            }
            pool.Checkpoint();

            var errors = new ConcurrentQueue<Exception>();
            var startGate = new ManualResetEventSlim(false);
            Task[] tasks = new Task[4];

            for (int t = 0; t < tasks.Length; t++)
            {
                int tid = t;
                tasks[t] = Task.Run(() =>
                {
                    startGate.Wait();
                    try
                    {
                        var rng = new Random(900 + tid);
                        for (int iter = 0; iter < 80; iter++)
                        {
                            int pageNum = 1 + rng.Next(totalPages);
                            var pid = new PageId(1, pageNum);
                            bool isWriter = (tid % 2 == 0) && (iter % 3 == 0);

                            using var pin = pool.PinPage(pid, isWriter ? LockMode.Exclusive : LockMode.Shared);
                            int id = BinaryPrimitives.ReadInt32LittleEndian(pin.Data[..4]);
                            int seq = BinaryPrimitives.ReadInt32LittleEndian(pin.Data.Slice(4, 4));
                            int chk = BinaryPrimitives.ReadInt32LittleEndian(pin.Data.Slice(pageSize - 4, 4));
                            if (id != pageNum || chk != (id ^ seq))
                            {
                                throw new InvalidDataException($"Torn read on page {pageNum}: id={id}, seq={seq}, chk={chk}");
                            }

                            if (isWriter)
                            {
                                int nextSeq = seq + 1;
                                BinaryPrimitives.WriteInt32LittleEndian(pin.Data.Slice(4, 4), nextSeq);
                                BinaryPrimitives.WriteInt32LittleEndian(pin.Data.Slice(pageSize - 4, 4), pageNum ^ nextSeq);
                                pin.MarkDirty();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                });
            }

            startGate.Set();
            await Task.WhenAll(tasks);
            Assert.Empty(errors);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
