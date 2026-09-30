namespace Glacier.Storage;

using System;
using System.Threading.Tasks;

public readonly record struct PageId(int FileId, int PageNumber);
public readonly record struct TxId(ulong Value);
public enum LockMode : byte { Shared, Exclusive }

public readonly ref struct PagePin
{
    public readonly PageId Id;
    public readonly Span<byte> Data;
    private readonly IBufferPool _pool;

    public PagePin(PageId id, Span<byte> data, IBufferPool pool)
    {
        Id = id;
        Data = data;
        _pool = pool;
    }

    public void Dispose() => _pool.UnpinPage(Id, isDirty: false);
    public void MarkDirty() => _pool.MarkDirty(Id);
}

public interface IBufferPool : IDisposable
{
    PagePin PinPage(PageId pageId, LockMode mode);
    PageId AllocatePage();
    void UnpinPage(PageId pageId, bool isDirty);
    void MarkDirty(PageId pageId);
    void FlushPage(PageId pageId);
    void Checkpoint();
}

public interface IWalTransactionEngine : IDisposable
{
    TxId BeginTransaction();
    void LogRecord(TxId txId, ReadOnlySpan<byte> payload);
    ValueTask CommitAsync(TxId txId);
    void Rollback(TxId txId);
}
