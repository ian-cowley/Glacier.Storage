namespace Glacier.Storage.Tests;

using System;
using System.Buffers.Binary;
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
}
