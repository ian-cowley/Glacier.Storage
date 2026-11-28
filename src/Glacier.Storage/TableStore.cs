namespace Glacier.Storage;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Formats.Arrow;
using Glacier.Formats.Parquet;
using Glacier.Storage.BufferPool;
using Glacier.Storage.Wal;

/// <summary>
/// High-level unified columnar and transactional table storage facade.
/// Seamlessly integrates <see cref="IBufferPool"/> page-backed storage, ACID WAL,
/// Glacier Compact Columnar IPC v1, and Parquet export.
/// </summary>
public sealed class TableStore : IDisposable
{
    private readonly record struct StoredBatchEntry(int ByteLength, PageId[] Pages);

    private readonly string _tableDirectory;
    private readonly ArrowSchema _schema;
    private readonly IBufferPool _bufferPool;
    private readonly IWalTransactionEngine _wal;
    private readonly bool _ownsBufferPool;
    private readonly bool _ownsWal;
    private readonly List<StoredBatchEntry> _committedBatches = new();
    private readonly Dictionary<TxId, List<byte[]>> _pendingTxBatches = new();
    private readonly object _sync = new();
    private int _disposed;

#if !USE_LOCAL_FORMATS
    private byte[]? _cachedSchemaBytes;
#endif

    /// <summary>Gets the table schema.</summary>
    public ArrowSchema Schema => _schema;

    /// <summary>Gets the backing buffer pool.</summary>
    public IBufferPool BufferPool => _bufferPool;

    /// <summary>Gets the backing WAL transaction engine.</summary>
    public IWalTransactionEngine Wal => _wal;

    /// <summary>Gets the number of committed record batches stored in this table.</summary>
    public int BatchCount { get { lock (_sync) return _committedBatches.Count; } }

    /// <summary>
    /// Initializes a new <see cref="TableStore"/> rooted at <paramref name="tableDirectory"/>.
    /// </summary>
    /// <param name="tableDirectory">Directory containing the table's write-ahead log and artifacts.</param>
    /// <param name="schema">Schema of all record batches written to this table.</param>
    /// <param name="bufferPool">Optional caller-provided buffer pool (when <c>null</c>, an internal pool is created and owned).</param>
    /// <param name="wal">Optional caller-provided WAL engine (when <c>null</c>, <c>table.wal</c> is opened and owned).</param>
    /// <param name="leaveBufferPoolOpen">When <paramref name="bufferPool"/> is supplied by the caller, <c>true</c> leaves it open upon disposal.</param>
    /// <param name="leaveWalOpen">When <paramref name="wal"/> is supplied by the caller, <c>true</c> leaves it open upon disposal.</param>
    public TableStore(
        string tableDirectory,
        ArrowSchema schema,
        IBufferPool? bufferPool = null,
        IWalTransactionEngine? wal = null,
        bool leaveBufferPoolOpen = true,
        bool leaveWalOpen = true)
    {
        _tableDirectory = tableDirectory ?? throw new ArgumentNullException(nameof(tableDirectory));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));

        Directory.CreateDirectory(_tableDirectory);

        BufferPool.BufferPool? createdPool = null;
        FileStream? createdWalStream = null;
        WalTransactionEngine? createdWal = null;

        try
        {
            if (bufferPool != null)
            {
                _bufferPool = bufferPool;
                _ownsBufferPool = !leaveBufferPoolOpen;
            }
            else
            {
                createdPool = new BufferPool.BufferPool(64, PageTier.Medium);
                _bufferPool = createdPool;
                _ownsBufferPool = true;
            }

            if (wal != null)
            {
                _wal = wal;
                _ownsWal = !leaveWalOpen;
            }
            else
            {
                string walPath = Path.Combine(_tableDirectory, "table.wal");
                createdWalStream = new FileStream(walPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                ulong initialLsn = 0;
                ulong initialTxId = 0;
                if (createdWalStream.Length > 0)
                {
                    (initialLsn, initialTxId) = ReplayCommittedWalRecords(createdWalStream);
                    createdWalStream.Seek(0, SeekOrigin.End);
                }

                createdWal = new WalTransactionEngine(createdWalStream, initialLsn: initialLsn, initialTxId: initialTxId);
                _wal = createdWal;
                createdWalStream = null; // owned by createdWal
                _ownsWal = true;
            }
        }
        catch
        {
            if (createdWal != null)
            {
                createdWal.Dispose();
            }
            else
            {
                createdWalStream?.Dispose();
            }

            createdPool?.Dispose();
            throw;
        }
    }

    private (ulong MaxLsn, ulong MaxTxId) ReplayCommittedWalRecords(Stream walStream)
    {
        var recovery = WalRecovery.Recover(walStream);
        foreach (var record in recovery.CommittedRecords)
        {
            if (record.Payload.Length <= 8)
            {
                continue;
            }

#if USE_LOCAL_FORMATS
            ReadOnlyMemory<byte> payload = record.Payload;
            ArrowSchema walSchema = ArrowStreamReader.ReadSchema(payload.Span, out int pos);
            while (pos < payload.Length)
            {
                var recoveredBatch = ArrowStreamReader.ReadRecordBatch(payload.Slice(pos), walSchema, out int consumed);
                if (recoveredBatch == null)
                {
                    break;
                }

                _committedBatches.Add(StoreBatchInBufferPool(payload.Span.Slice(pos, consumed)));
                pos += consumed;
            }
#else
            using var ms = new MemoryStream(record.Payload.ToArray(), writable: false);
            using var reader = new ArrowStreamReader(ms);
            while (reader.ReadNextRecordBatch() is { } recoveredBatch)
            {
                using var batchMs = new MemoryStream();
                using (var batchWriter = new ArrowStreamWriter(batchMs, leaveOpen: true))
                {
                    batchWriter.WriteRecordBatch(recoveredBatch);
                }
                _committedBatches.Add(StoreBatchInBufferPool(batchMs.GetBuffer().AsSpan(0, (int)batchMs.Length)));
            }
#endif
        }
        return (recovery.MaxLsn, recovery.MaxTxId);
    }

    /// <summary>
    /// Begins a new WAL transaction.
    /// </summary>
    /// <returns>The assigned <see cref="TxId"/>.</returns>
    public TxId BeginTransaction()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _wal.BeginTransaction();
    }

    /// <summary>
    /// Serializes <paramref name="batch"/>, logs it to the WAL under <paramref name="txId"/>,
    /// and stages it until transaction commit.
    /// </summary>
    /// <param name="txId">Active transaction identifier.</param>
    /// <param name="batch">Record batch to append.</param>
    public void AppendBatch(TxId txId, ArrowRecordBatch batch)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(batch);

        byte[] batchBytes;
#if USE_LOCAL_FORMATS
        using var ipcWriterBuf = new PooledBufferWriter(4096);
        ArrowStreamWriter.WriteSchema(ipcWriterBuf, _schema);
        int schemaLen = ipcWriterBuf.WrittenCount;
        ArrowStreamWriter.WriteRecordBatch(ipcWriterBuf, batch);
        int batchLen = ipcWriterBuf.WrittenCount - schemaLen;
        ArrowStreamWriter.WriteEndOfStream(ipcWriterBuf);

        ReadOnlySpan<byte> fullSpan = ipcWriterBuf.WrittenSpan;
        _wal.LogRecord(txId, fullSpan);
        batchBytes = fullSpan.Slice(schemaLen, batchLen).ToArray();
#else
        using var ipcMs = new MemoryStream();
        int schemaLen;
        int batchLen;
        using (var ipcWriter = new ArrowStreamWriter(ipcMs, leaveOpen: true))
        {
            ipcWriter.WriteSchema(_schema);
            schemaLen = (int)ipcMs.Position;
            ipcWriter.WriteRecordBatch(batch);
            batchLen = (int)ipcMs.Position - schemaLen;
            ipcWriter.WriteEndOfStream();
        }

        ReadOnlySpan<byte> fullSpan = ipcMs.GetBuffer().AsSpan(0, (int)ipcMs.Length);
        _wal.LogRecord(txId, fullSpan);
        batchBytes = fullSpan.Slice(schemaLen, batchLen).ToArray();
#endif

        lock (_sync)
        {
            if (!_pendingTxBatches.TryGetValue(txId, out var list))
            {
                list = new List<byte[]>(1);
                _pendingTxBatches[txId] = list;
            }
            list.Add(batchBytes);
        }
    }

    /// <summary>
    /// Commits <paramref name="txId"/> in the WAL and pages its staged batches into the <see cref="IBufferPool"/>.
    /// </summary>
    /// <param name="txId">Transaction identifier to commit.</param>
    /// <returns>A <see cref="ValueTask"/> completing when the commit is durable.</returns>
    public async ValueTask CommitTransactionAsync(TxId txId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _wal.CommitAsync(txId).ConfigureAwait(false);

        lock (_sync)
        {
            if (_pendingTxBatches.Remove(txId, out var committed))
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    _committedBatches.Add(StoreBatchInBufferPool(committed[i]));
                }
            }
        }
    }

    /// <summary>
    /// Rolls back <paramref name="txId"/> in the WAL and discards staged batches.
    /// </summary>
    /// <param name="txId">Transaction identifier to roll back.</param>
    public void RollbackTransaction(TxId txId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _wal.Rollback(txId);
        lock (_sync)
        {
            _pendingTxBatches.Remove(txId);
        }
    }

    /// <summary>
    /// Reads and reconstructs the committed <see cref="ArrowRecordBatch"/> at <paramref name="index"/>
    /// from its <see cref="IBufferPool"/> pages.
    /// </summary>
    /// <param name="index">Zero-based committed batch index.</param>
    /// <returns>The reconstructed <see cref="ArrowRecordBatch"/>.</returns>
    public ArrowRecordBatch ReadBatch(int index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        StoredBatchEntry entry;
        lock (_sync)
        {
            if ((uint)index >= (uint)_committedBatches.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            entry = _committedBatches[index];
        }

        return ReadBatchFromBufferPool(entry);
    }

    /// <summary>
    /// Exports all committed batches to a Parquet v1 file at <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">Destination Parquet file path.</param>
    /// <param name="codec">Compression codec for column chunks.</param>
    public void ExportToParquet(string filePath, CompressionCodec codec = CompressionCodec.Snappy)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_sync)
        {
            if (_committedBatches.Count == 0) return;
            var batches = new List<ArrowRecordBatch>(_committedBatches.Count);
            for (int i = 0; i < _committedBatches.Count; i++)
            {
                batches.Add(ReadBatchFromBufferPool(_committedBatches[i]));
            }
            ParquetWriter.WriteFile(filePath, batches, codec);
        }
    }

    /// <summary>
    /// Streams all committed batches directly from <see cref="IBufferPool"/> pages to <paramref name="stream"/>
    /// using Glacier Compact Columnar IPC v1 framing.
    /// </summary>
    /// <param name="stream">Destination writable stream.</param>
    public void ExportToArrowIpc(Stream stream)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(stream);

        lock (_sync)
        {
            using var writer = new ArrowStreamWriter(stream, leaveOpen: true);
            writer.WriteSchema(_schema);
            for (int b = 0; b < _committedBatches.Count; b++)
            {
                StoredBatchEntry entry = _committedBatches[b];
                int offset = 0;
                for (int p = 0; p < entry.Pages.Length && offset < entry.ByteLength; p++)
                {
                    using var pin = _bufferPool.PinPage(entry.Pages[p], LockMode.Shared);
                    int chunk = Math.Min(entry.ByteLength - offset, pin.Data.Length);
                    stream.Write(pin.Data.Slice(0, chunk));
                    offset += chunk;
                }
            }
            writer.WriteEndOfStream();
        }
    }

    private StoredBatchEntry StoreBatchInBufferPool(ReadOnlySpan<byte> batchBytes)
    {
        if (batchBytes.IsEmpty)
        {
            return new StoredBatchEntry(0, Array.Empty<PageId>());
        }

        var pages = new List<PageId>();
        int offset = 0;
        while (offset < batchBytes.Length)
        {
            PageId pageId = _bufferPool.AllocatePage();
            using (var pin = _bufferPool.PinPage(pageId, LockMode.Exclusive))
            {
                int chunk = Math.Min(batchBytes.Length - offset, pin.Data.Length);
                batchBytes.Slice(offset, chunk).CopyTo(pin.Data);
                pin.MarkDirty();
                offset += chunk;
            }
            pages.Add(pageId);
        }

        return new StoredBatchEntry(batchBytes.Length, pages.ToArray());
    }

    private ArrowRecordBatch ReadBatchFromBufferPool(StoredBatchEntry entry)
    {
        byte[] batchBytes = GC.AllocateUninitializedArray<byte>(entry.ByteLength);
        int offset = 0;
        for (int p = 0; p < entry.Pages.Length && offset < entry.ByteLength; p++)
        {
            using var pin = _bufferPool.PinPage(entry.Pages[p], LockMode.Shared);
            int chunk = Math.Min(entry.ByteLength - offset, pin.Data.Length);
            pin.Data.Slice(0, chunk).CopyTo(batchBytes.AsSpan(offset, chunk));
            offset += chunk;
        }

#if USE_LOCAL_FORMATS
        return ArrowStreamReader.ReadRecordBatch(batchBytes, _schema, out _)
            ?? throw new InvalidDataException("Unexpected end-of-stream marker in stored RecordBatch.");
#else
        byte[] schemaBytes = GetSerializedSchemaBytes();
        using var ms = new MemoryStream(schemaBytes.Length + batchBytes.Length + 8);
        ms.Write(schemaBytes);
        ms.Write(batchBytes);
        using (var eosWriter = new ArrowStreamWriter(ms, leaveOpen: true))
        {
            eosWriter.WriteEndOfStream();
        }
        ms.Position = 0;
        using var reader = new ArrowStreamReader(ms);
        return reader.ReadNextRecordBatch()
            ?? throw new InvalidDataException("Unexpected end-of-stream marker in stored RecordBatch.");
#endif
    }

#if !USE_LOCAL_FORMATS
    private byte[] GetSerializedSchemaBytes()
    {
        if (_cachedSchemaBytes != null)
        {
            return _cachedSchemaBytes;
        }

        using var ms = new MemoryStream();
        using (var writer = new ArrowStreamWriter(ms, leaveOpen: true))
        {
            writer.WriteSchema(_schema);
        }
        _cachedSchemaBytes = ms.ToArray();
        return _cachedSchemaBytes;
    }
#else
    private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer;
        private int _written;

        public PooledBufferWriter(int initialCapacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        }

        public int WrittenCount => _written;
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || _written + count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            int required = Math.Max(sizeHint, 1);
            if (_buffer.Length - _written >= required)
            {
                return;
            }

            int newSize = Math.Max(_buffer.Length * 2, checked(_written + required));
            byte[] next = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_buffer, 0, next, 0, _written);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            byte[] toReturn = Interlocked.Exchange(ref _buffer!, null!);
            if (toReturn != null)
            {
                ArrayPool<byte>.Shared.Return(toReturn);
            }
        }
    }
#endif

    /// <summary>
    /// Releases owned <see cref="Wal"/> and <see cref="BufferPool"/> resources according to ownership flags.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsWal)
        {
            _wal.Dispose();
        }

        if (_ownsBufferPool)
        {
            _bufferPool.Dispose();
        }
    }
}
