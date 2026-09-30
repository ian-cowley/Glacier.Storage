namespace Glacier.Storage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Glacier.Storage.Arrow;
using Glacier.Storage.BufferPool;
using Glacier.Storage.Parquet;
using Glacier.Storage.Wal;

/// <summary>
/// High-level unified columnar and transactional table storage facade.
/// Seamlessly integrates BufferPool, ACID WAL, Arrow IPC, and Parquet.
/// </summary>
public sealed class TableStore : IDisposable
{
    private readonly string _tableDirectory;
    private readonly ArrowSchema _schema;
    private readonly IBufferPool _bufferPool;
    private readonly IWalTransactionEngine _wal;
    private readonly List<ArrowRecordBatch> _batches = new();
    private readonly object _sync = new();
    private bool _isDisposed;

    public ArrowSchema Schema => _schema;
    public IBufferPool BufferPool => _bufferPool;
    public IWalTransactionEngine Wal => _wal;
    public int BatchCount { get { lock (_sync) return _batches.Count; } }

    public TableStore(string tableDirectory, ArrowSchema schema, IBufferPool? bufferPool = null, IWalTransactionEngine? wal = null)
    {
        _tableDirectory = tableDirectory;
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));

        Directory.CreateDirectory(_tableDirectory);

        _bufferPool = bufferPool ?? new BufferPool.BufferPool(64, PageTier.Medium);

        if (wal != null)
        {
            _wal = wal;
        }
        else
        {
            string walPath = Path.Combine(_tableDirectory, "table.wal");
            var walStream = new FileStream(walPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            _wal = new WalTransactionEngine(walStream);
        }
    }

    public TxId BeginTransaction() => _wal.BeginTransaction();

    public void AppendBatch(TxId txId, ArrowRecordBatch batch)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_sync)
        {
            _batches.Add(batch);
        }

        // Log append in WAL
        Span<byte> payload = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload[..4], batch.RowCount);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(4, 4), batch.Columns.Count);
        _wal.LogRecord(txId, payload);
    }

    public async ValueTask CommitTransactionAsync(TxId txId)
    {
        await _wal.CommitAsync(txId).ConfigureAwait(false);
    }

    public void RollbackTransaction(TxId txId)
    {
        _wal.Rollback(txId);
    }

    public void ExportToParquet(string filePath, CompressionCodec codec = CompressionCodec.Snappy)
    {
        lock (_sync)
        {
            if (_batches.Count == 0) return;
            // Write first batch or combined batch
            ParquetWriter.WriteFile(filePath, _batches[0], codec);
        }
    }

    public void ExportToArrowIpc(Stream stream)
    {
        lock (_sync)
        {
            using var writer = new ArrowStreamWriter(stream, leaveOpen: true);
            writer.WriteSchema(_schema);
            foreach (var b in _batches)
            {
                writer.WriteRecordBatch(b);
            }
            writer.WriteEndOfStream();
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _wal.Dispose();
            _bufferPool.Dispose();
        }
    }
}
