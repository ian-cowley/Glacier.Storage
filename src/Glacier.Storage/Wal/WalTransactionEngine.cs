namespace Glacier.Storage.Wal;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Double-buffered group-commit write-ahead log (WAL) transaction engine.
/// Appends serialize into an active ring buffer under <c>_appendLock</c> while background
/// and explicit flushes swap buffers in O(1) and perform disk I/O + <c>FlushToDisk</c> outside <c>_appendLock</c>.
/// </summary>
public sealed class WalTransactionEngine : IWalTransactionEngine
{
    private readonly Stream _logStream;
    private readonly FileStream? _fileStream;
    private readonly int _bufferCapacity;
    private byte[] _activeBuffer;
    private byte[] _flushBuffer;
    private int _activeBufferOffset;
    private ulong _activeMaxLsn;
    private long _fileWriteOffset;

    private ulong _currentLsn;
    private ulong _flushedLsn;
    private ulong _nextTxId;

    // Strict lock hierarchy: _flushLock (outer) -> _appendLock (inner).
    private readonly object _appendLock = new();
    private readonly object _flushLock = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<bool>> _pendingCommits = new();

    private readonly Timer _groupCommitTimer;
    private readonly bool _leaveOpen;
    private int _disposed;

    /// <summary>Gets the highest log sequence number assigned so far.</summary>
    public ulong CurrentLsn => Volatile.Read(ref _currentLsn);

    /// <summary>Gets the highest log sequence number durably flushed to the underlying log stream.</summary>
    public ulong FlushedLsn => Volatile.Read(ref _flushedLsn);

    /// <summary>
    /// Initializes a new <see cref="WalTransactionEngine"/> over <paramref name="logStream"/>.
    /// </summary>
    /// <param name="logStream">Writable WAL destination stream.</param>
    /// <param name="bufferCapacity">Capacity in bytes of each active/flush buffer (minimum 4,096).</param>
    /// <param name="groupCommitIntervalMs">Periodic group-commit timer interval in milliseconds.</param>
    /// <param name="leaveOpen"><c>true</c> to leave <paramref name="logStream"/> open upon disposal.</param>
    /// <param name="initialLsn">Initial LSN seed when resuming an existing WAL.</param>
    /// <param name="initialTxId">Initial transaction ID seed when resuming an existing WAL.</param>
    public WalTransactionEngine(
        Stream logStream,
        int bufferCapacity = 65536,
        int groupCommitIntervalMs = 5,
        bool leaveOpen = false,
        ulong initialLsn = 0,
        ulong initialTxId = 0)
    {
        _logStream = logStream ?? throw new ArgumentNullException(nameof(logStream));
        _fileStream = logStream as FileStream;
        if (_fileStream != null && _fileStream.CanSeek)
        {
            _fileStream.Flush();
            _fileWriteOffset = _fileStream.Position;
        }

        _bufferCapacity = Math.Max(4096, bufferCapacity);
        _leaveOpen = leaveOpen;
        _currentLsn = initialLsn;
        _flushedLsn = initialLsn;
        _nextTxId = initialTxId;
        _activeBuffer = new byte[_bufferCapacity];
        _flushBuffer = new byte[_bufferCapacity];
        _groupCommitTimer = new Timer(_ => FlushInternal(allowWhenDisposing: false), null, groupCommitIntervalMs, groupCommitIntervalMs);
    }

    /// <inheritdoc />
    public TxId BeginTransaction()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ulong txVal = Interlocked.Increment(ref _nextTxId);
        var txId = new TxId(txVal);

        AppendLogRecord(txId, WalRecordType.Begin, ReadOnlySpan<byte>.Empty);
        return txId;
    }

    /// <inheritdoc />
    public void LogRecord(TxId txId, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        AppendLogRecord(txId, WalRecordType.Update, payload);
    }

    /// <inheritdoc />
    public ValueTask CommitAsync(TxId txId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ulong commitLsn = AppendLogRecord(txId, WalRecordType.Commit, ReadOnlySpan<byte>.Empty);

        if (Volatile.Read(ref _flushedLsn) >= commitLsn)
        {
            return ValueTask.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommits[commitLsn] = tcs;

        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_pendingCommits.TryRemove(commitLsn, out _))
            {
                tcs.TrySetException(new ObjectDisposedException(nameof(WalTransactionEngine)));
            }
            return new ValueTask(tcs.Task);
        }

        // Trigger immediate flush
        FlushInternal(allowWhenDisposing: false);

        if (Volatile.Read(ref _flushedLsn) >= commitLsn)
        {
            _pendingCommits.TryRemove(commitLsn, out _);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(tcs.Task);
    }

    /// <inheritdoc />
    public void Rollback(TxId txId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        AppendLogRecord(txId, WalRecordType.Abort, ReadOnlySpan<byte>.Empty);
    }

    private ulong AppendLogRecord(TxId txId, WalRecordType type, ReadOnlySpan<byte> payload)
    {
        int recordSize = WalRecord.GetSerializedSize(payload.Length);

        if (recordSize > _bufferCapacity)
        {
            byte[] largeBuf = ArrayPool<byte>.Shared.Rent(recordSize);
            try
            {
                lock (_flushLock)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                    int pendingBytes;
                    ulong pendingMaxLsn;
                    ulong lsn;

                    lock (_appendLock)
                    {
                        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                        pendingBytes = _activeBufferOffset;
                        pendingMaxLsn = _activeMaxLsn;
                        if (pendingBytes > 0)
                        {
                            (_activeBuffer, _flushBuffer) = (_flushBuffer, _activeBuffer);
                            _activeBufferOffset = 0;
                        }

                        lsn = ++_currentLsn;
                        _activeMaxLsn = Math.Max(_activeMaxLsn, lsn);
                    }

                    WalRecord.Serialize(lsn, txId, type, payload, largeBuf.AsSpan(0, recordSize));

                    if (pendingBytes > 0)
                    {
                        WriteToLog(_flushBuffer.AsSpan(0, pendingBytes));
                    }

                    WriteToLog(largeBuf.AsSpan(0, recordSize));
                    FlushStreamToDisk();

                    UpdateFlushedLsn(Math.Max(pendingMaxLsn, lsn));
                    return lsn;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(largeBuf);
            }
        }

        while (true)
        {
            lock (_appendLock)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                if (_activeBufferOffset + recordSize <= _bufferCapacity)
                {
                    ulong lsn = ++_currentLsn;
                    Span<byte> dest = _activeBuffer.AsSpan(_activeBufferOffset, recordSize);
                    WalRecord.Serialize(lsn, txId, type, payload, dest);
                    _activeBufferOffset += recordSize;
                    _activeMaxLsn = lsn;
                    return lsn;
                }
            }

            FlushInternal(allowWhenDisposing: false);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteToLog(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        if (_fileStream != null && _fileStream.CanSeek)
        {
            RandomAccess.Write(_fileStream.SafeFileHandle, data, _fileWriteOffset);
            _fileWriteOffset += data.Length;
        }
        else
        {
            _logStream.Write(data);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushStreamToDisk()
    {
        if (_fileStream != null)
        {
            RandomAccess.FlushToDisk(_fileStream.SafeFileHandle);
        }
        else
        {
            _logStream.Flush();
        }
    }

    /// <summary>
    /// Flushes all buffered WAL records to the underlying stream and advances <see cref="FlushedLsn"/>.
    /// </summary>
    public void Flush()
    {
        FlushInternal(allowWhenDisposing: false);
    }

    private void FlushInternal(bool allowWhenDisposing)
    {
        if (!allowWhenDisposing && Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_flushLock)
        {
            if (!allowWhenDisposing && Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            int bytesToWrite;
            ulong maxSwappedLsn;

            lock (_appendLock)
            {
                bytesToWrite = _activeBufferOffset;
                if (bytesToWrite == 0)
                {
                    return;
                }

                (_activeBuffer, _flushBuffer) = (_flushBuffer, _activeBuffer);
                maxSwappedLsn = _activeMaxLsn;
                _activeBufferOffset = 0;
            }

            WriteToLog(_flushBuffer.AsSpan(0, bytesToWrite));
            FlushStreamToDisk();
            UpdateFlushedLsn(maxSwappedLsn);
        }
    }

    private void UpdateFlushedLsn(ulong candidateLsn)
    {
        ulong currentFlushed = Volatile.Read(ref _flushedLsn);
        if (candidateLsn > currentFlushed)
        {
            Volatile.Write(ref _flushedLsn, candidateLsn);
            currentFlushed = candidateLsn;
        }

        NotifyPendingCommits(currentFlushed);
    }

    private void NotifyPendingCommits(ulong flushedLsn)
    {
        foreach (var kvp in _pendingCommits)
        {
            if (kvp.Key <= flushedLsn)
            {
                if (_pendingCommits.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetResult(true);
                }
            }
        }
    }

    /// <summary>
    /// Stops the group-commit timer, flushes remaining buffered records, faults any uncompleted commit waiters,
    /// and disposes the underlying stream unless <c>leaveOpen</c> was specified.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var timerDone = new ManualResetEvent(false);
        if (_groupCommitTimer.Dispose(timerDone))
        {
            timerDone.WaitOne();
        }

        try
        {
            FlushInternal(allowWhenDisposing: true);
        }
        finally
        {
            foreach (var kvp in _pendingCommits)
            {
                if (_pendingCommits.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetException(new ObjectDisposedException(nameof(WalTransactionEngine)));
                }
            }

            if (!_leaveOpen)
            {
                _logStream.Dispose();
            }
        }
    }
}
