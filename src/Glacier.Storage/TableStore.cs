namespace Glacier.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Glacier.Formats.Arrow;
using Glacier.Storage.BufferPool;
using Glacier.Formats.Parquet;
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
    private readonly Dictionary<TxId, List<ArrowRecordBatch>> _pendingTxBatches = new();
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
            if (walStream.Length > 0)
            {
                ReplayCommittedWalRecords(walStream);
                walStream.Seek(0, SeekOrigin.End);
            }
            _wal = new WalTransactionEngine(walStream);
        }
    }

    private void ReplayCommittedWalRecords(Stream walStream)
    {
        var recovery = WalRecovery.Recover(walStream);
        foreach (var record in recovery.CommittedRecords)
        {
            if (record.Payload.Length <= 8)
            {
                continue;
            }

            using var ms = new MemoryStream(record.Payload.ToArray(), writable: false);
            using var reader = new ArrowStreamReader(ms);
            while (reader.ReadNextRecordBatch() is { } recoveredBatch)
            {
                _batches.Add(recoveredBatch);
            }
        }
    }

    public TxId BeginTransaction() => _wal.BeginTransaction();

    public void AppendBatch(TxId txId, ArrowRecordBatch batch)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(batch);

        using var ipcMs = new MemoryStream();
        using (var ipcWriter = new ArrowStreamWriter(ipcMs, leaveOpen: true))
        {
            ipcWriter.WriteSchema(_schema);
            ipcWriter.WriteRecordBatch(batch);
            ipcWriter.WriteEndOfStream();
        }

        _wal.LogRecord(txId, ipcMs.GetBuffer().AsSpan(0, (int)ipcMs.Length));

        lock (_sync)
        {
            _batches.Add(batch);
            if (!_pendingTxBatches.TryGetValue(txId, out var list))
            {
                list = new List<ArrowRecordBatch>(1);
                _pendingTxBatches[txId] = list;
            }
            list.Add(batch);
        }
    }

    public async ValueTask CommitTransactionAsync(TxId txId)
    {
        await _wal.CommitAsync(txId).ConfigureAwait(false);
        lock (_sync)
        {
            _pendingTxBatches.Remove(txId);
        }
    }

    public void RollbackTransaction(TxId txId)
    {
        _wal.Rollback(txId);
        lock (_sync)
        {
            if (_pendingTxBatches.Remove(txId, out var rolledBack))
            {
                foreach (var b in rolledBack)
                {
                    _batches.Remove(b);
                }
            }
        }
    }

    public void ExportToParquet(string filePath, CompressionCodec codec = CompressionCodec.Snappy)
    {
        lock (_sync)
        {
            if (_batches.Count == 0) return;
            ParquetWriter.WriteFile(filePath, _batches, codec);
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
