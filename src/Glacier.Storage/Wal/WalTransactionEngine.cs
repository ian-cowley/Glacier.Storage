namespace Glacier.Storage.Wal;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public sealed class WalTransactionEngine : IWalTransactionEngine
{
    private readonly Stream _logStream;
    private readonly int _bufferCapacity;
    private readonly byte[] _activeBuffer;
    private readonly byte[] _flushBuffer;
    private int _activeBufferOffset;

    private ulong _currentLsn;
    private ulong _flushedLsn;
    private ulong _nextTxId;

    private readonly object _appendLock = new();
    private readonly object _flushLock = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<bool>> _pendingCommits = new();
    private readonly ConcurrentDictionary<ulong, ulong> _txCommitLsn = new();

    private readonly Timer _groupCommitTimer;
    private readonly bool _leaveOpen;
    private bool _isDisposed;

    public ulong CurrentLsn => Volatile.Read(ref _currentLsn);
    public ulong FlushedLsn => Volatile.Read(ref _flushedLsn);

    public WalTransactionEngine(Stream logStream, int bufferCapacity = 65536, int groupCommitIntervalMs = 5, bool leaveOpen = false)
    {
        _logStream = logStream ?? throw new ArgumentNullException(nameof(logStream));
        _bufferCapacity = Math.Max(4096, bufferCapacity);
        _leaveOpen = leaveOpen;
        _activeBuffer = new byte[_bufferCapacity];
        _flushBuffer = new byte[_bufferCapacity];
        _groupCommitTimer = new Timer(_ => FlushInternal(), null, groupCommitIntervalMs, groupCommitIntervalMs);
    }

    public TxId BeginTransaction()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ulong txVal = Interlocked.Increment(ref _nextTxId);
        var txId = new TxId(txVal);

        AppendLogRecord(txId, WalRecordType.Begin, ReadOnlySpan<byte>.Empty);
        return txId;
    }

    public void LogRecord(TxId txId, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        AppendLogRecord(txId, WalRecordType.Update, payload);
    }

    public ValueTask CommitAsync(TxId txId)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ulong commitLsn = AppendLogRecord(txId, WalRecordType.Commit, ReadOnlySpan<byte>.Empty);

        if (Volatile.Read(ref _flushedLsn) >= commitLsn)
        {
            return ValueTask.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommits[commitLsn] = tcs;

        // Trigger immediate flush if needed
        FlushInternal();

        if (Volatile.Read(ref _flushedLsn) >= commitLsn)
        {
            _pendingCommits.TryRemove(commitLsn, out _);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(tcs.Task);
    }

    public void Rollback(TxId txId)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        AppendLogRecord(txId, WalRecordType.Abort, ReadOnlySpan<byte>.Empty);
    }

    private ulong AppendLogRecord(TxId txId, WalRecordType type, ReadOnlySpan<byte> payload)
    {
        lock (_appendLock)
        {
            ulong lsn = ++_currentLsn;
            int recordSize = WalRecord.GetSerializedSize(payload.Length);

            if (_activeBufferOffset + recordSize > _bufferCapacity)
            {
                FlushActiveBufferLocked();
            }

            Span<byte> dest = _activeBuffer.AsSpan(_activeBufferOffset, recordSize);
            WalRecord.Serialize(lsn, txId, type, payload, dest);
            _activeBufferOffset += recordSize;

            return lsn;
        }
    }

    private void FlushActiveBufferLocked()
    {
        if (_activeBufferOffset == 0) return;

        lock (_flushLock)
        {
            Buffer.BlockCopy(_activeBuffer, 0, _flushBuffer, 0, _activeBufferOffset);
            int bytesToWrite = _activeBufferOffset;
            _activeBufferOffset = 0;

            _logStream.Write(_flushBuffer, 0, bytesToWrite);
            _logStream.Flush();

            ulong flushed = Volatile.Read(ref _currentLsn);
            Volatile.Write(ref _flushedLsn, flushed);

            NotifyPendingCommits(flushed);
        }
    }

    public void Flush()
    {
        FlushInternal();
    }

    private void FlushInternal()
    {
        if (_isDisposed) return;

        lock (_appendLock)
        {
            if (_activeBufferOffset == 0) return;
            FlushActiveBufferLocked();
        }
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

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _groupCommitTimer.Dispose();
            FlushInternal();
            _isDisposed = true;
            if (!_leaveOpen)
            {
                _logStream.Dispose();
            }
        }
    }
}
