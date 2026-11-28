namespace Glacier.Storage;

using System;
using System.Threading.Tasks;

/// <summary>
/// Logical page identifier consisting of a file identifier and zero-based page number.
/// </summary>
/// <param name="FileId">The storage file identifier.</param>
/// <param name="PageNumber">The non-negative page number within the file.</param>
public readonly record struct PageId(int FileId, int PageNumber);

/// <summary>
/// Monotonic transaction identifier issued by an <see cref="IWalTransactionEngine"/>.
/// </summary>
/// <param name="Value">The numeric transaction identifier.</param>
public readonly record struct TxId(ulong Value);

/// <summary>
/// Latch acquisition mode for pinned buffer pool pages.
/// </summary>
public enum LockMode : byte
{
    /// <summary>Multiple concurrent readers allowed.</summary>
    Shared,

    /// <summary>Exclusive single-writer lock.</summary>
    Exclusive
}

/// <summary>
/// Scoped RAII handle representing a pinned page in an <see cref="IBufferPool"/>.
/// Disposing releases the acquired latch and decrements the frame pin count.
/// </summary>
public ref struct PagePin
{
    /// <summary>Gets the identifier of the pinned page.</summary>
    public readonly PageId Id;

    /// <summary>Gets the byte span over the pinned page memory.</summary>
    public readonly Span<byte> Data;

    /// <summary>Gets the latch mode held by this pin.</summary>
    public readonly LockMode Mode;

    private readonly IBufferPool? _pool;
    private readonly bool _hasExplicitMode;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new <see cref="PagePin"/> without an explicit lock mode (uses legacy untyped unpin).
    /// </summary>
    /// <param name="id">The pinned page identifier.</param>
    /// <param name="data">The page memory span.</param>
    /// <param name="pool">The owning buffer pool.</param>
    public PagePin(PageId id, Span<byte> data, IBufferPool pool)
    {
        Id = id;
        Data = data;
        Mode = LockMode.Shared;
        _pool = pool;
        _hasExplicitMode = false;
        _isDisposed = false;
    }

    /// <summary>
    /// Initializes a new <see cref="PagePin"/> tracking an explicit <see cref="LockMode"/>.
    /// </summary>
    /// <param name="id">The pinned page identifier.</param>
    /// <param name="data">The page memory span.</param>
    /// <param name="pool">The owning buffer pool.</param>
    /// <param name="mode">The latch mode held for this pin.</param>
    public PagePin(PageId id, Span<byte> data, IBufferPool pool, LockMode mode)
    {
        Id = id;
        Data = data;
        Mode = mode;
        _pool = pool;
        _hasExplicitMode = true;
        _isDisposed = false;
    }

    /// <summary>
    /// Unpins the page and releases its latch exactly once.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed || _pool == null)
        {
            return;
        }

        _isDisposed = true;
        if (_hasExplicitMode)
        {
            _pool.UnpinPage(Id, Mode, isDirty: false);
        }
        else
        {
            _pool.UnpinPage(Id, isDirty: false);
        }
    }

    /// <summary>
    /// Marks the pinned page as dirty so it will be flushed before eviction or checkpoint.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if this <see cref="PagePin"/> has already been disposed.</exception>
    public readonly void MarkDirty()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(PagePin));
        _pool?.MarkDirty(Id);
    }
}

/// <summary>
/// Abstraction over a fixed-capacity page buffer pool.
/// </summary>
public interface IBufferPool : IDisposable
{
    /// <summary>
    /// Pins <paramref name="pageId"/> into memory and acquires a latch in <paramref name="mode"/>.
    /// </summary>
    /// <param name="pageId">The page to pin.</param>
    /// <param name="mode">The latch mode to acquire.</param>
    /// <returns>A scoped <see cref="PagePin"/> handle.</returns>
    PagePin PinPage(PageId pageId, LockMode mode);

    /// <summary>
    /// Allocates a new zero-initialized dirty page in the buffer pool.
    /// </summary>
    /// <returns>The newly allocated <see cref="PageId"/>.</returns>
    PageId AllocatePage();

    /// <summary>
    /// Unpins <paramref name="pageId"/>, optionally marking it dirty.
    /// </summary>
    /// <param name="pageId">The page to unpin.</param>
    /// <param name="isDirty"><c>true</c> to mark the page dirty before unpinning.</param>
    void UnpinPage(PageId pageId, bool isDirty);

    /// <summary>
    /// Unpins <paramref name="pageId"/> and releases the specified <paramref name="mode"/> latch.
    /// </summary>
    /// <param name="pageId">The page to unpin.</param>
    /// <param name="mode">The latch mode originally acquired.</param>
    /// <param name="isDirty"><c>true</c> to mark the page dirty before unpinning.</param>
    void UnpinPage(PageId pageId, LockMode mode, bool isDirty) => UnpinPage(pageId, isDirty);

    /// <summary>
    /// Marks <paramref name="pageId"/> as dirty if currently resident in the buffer pool.
    /// </summary>
    /// <param name="pageId">The page identifier.</param>
    void MarkDirty(PageId pageId);

    /// <summary>
    /// Flushes a single resident page to backing storage.
    /// </summary>
    /// <param name="pageId">The page identifier to flush.</param>
    void FlushPage(PageId pageId);

    /// <summary>
    /// Flushes all dirty resident pages to backing storage.
    /// </summary>
    void Checkpoint();
}

/// <summary>
/// Transactional write-ahead logging (WAL) engine contract.
/// </summary>
public interface IWalTransactionEngine : IDisposable
{
    /// <summary>
    /// Begins a new transaction and writes a WAL begin record.
    /// </summary>
    /// <returns>The newly assigned <see cref="TxId"/>.</returns>
    TxId BeginTransaction();

    /// <summary>
    /// Appends an update payload record for an active transaction.
    /// </summary>
    /// <param name="txId">The transaction identifier.</param>
    /// <param name="payload">The record payload bytes.</param>
    void LogRecord(TxId txId, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Appends a commit record and awaits group-commit durability to the underlying log stream.
    /// </summary>
    /// <param name="txId">The transaction identifier to commit.</param>
    /// <returns>A <see cref="ValueTask"/> that completes once the commit record is flushed.</returns>
    ValueTask CommitAsync(TxId txId);

    /// <summary>
    /// Appends an abort record for the specified transaction.
    /// </summary>
    /// <param name="txId">The transaction identifier to roll back.</param>
    void Rollback(TxId txId);
}
