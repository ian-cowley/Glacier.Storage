namespace Glacier.Storage.BufferPool;

using System;
using System.Collections.Generic;

public interface IPageReplacementPolicy
{
    void RecordAccess(BufferFrame frame);
    int SelectVictim(BufferFrame[] frames);
    void OnEvict(BufferFrame frame);
}

/// <summary>
/// Clock-Pro adaptive page replacement algorithm.
/// Separates pages into Cold, Hot, and Test (non-resident history) pages.
/// Dynamically adjusts cold capacity to prevent sequential scan pollution while protecting hot working sets.
/// </summary>
public sealed class ClockProPolicy : IPageReplacementPolicy
{
    private readonly int _totalFrames;
    private int _targetCold;
    private int _handCold;
    private int _handHot;

    private int _coldCount;
    private int _hotCount;

    // Non-resident test page history (PageId -> timestamp/hit indicator)
    private readonly HashSet<PageId> _testPages = new();
    private readonly Queue<PageId> _testQueue = new();
    private readonly int _maxTestPages;
    private readonly object _sync = new();

    public ClockProPolicy(int totalFrames)
    {
        _totalFrames = Math.Max(1, totalFrames);
        _targetCold = Math.Max(1, _totalFrames / 4); // Initial target: 25% cold, 75% hot
        _maxTestPages = _totalFrames;
        _handCold = 0;
        _handHot = 0;
    }

    public int ColdCount => _coldCount;
    public int HotCount => _hotCount;
    public int TargetCold => _targetCold;
    public int TestPageCount { get { lock (_sync) return _testPages.Count; } }

    public void RecordAccess(BufferFrame frame)
    {
        lock (_sync)
        {
            if (frame.Status == PageStatus.Empty)
            {
                // Check if page was in test history
                if (_testPages.Remove(frame.PageId))
                {
                    // Access to test page! Cold capacity was too small or page is hot
                    _targetCold = Math.Min(_totalFrames - 1, _targetCold + 1);
                    frame.Status = PageStatus.Hot;
                    frame.RefBit = 0;
                    _hotCount++;
                }
                else
                {
                    // Newly loaded cold page
                    frame.Status = PageStatus.Cold;
                    frame.RefBit = 1;
                    _coldCount++;
                }
            }
            else
            {
                // Page is resident, set reference bit
                frame.RefBit = 1;
            }
        }
    }

    public int SelectVictim(BufferFrame[] frames)
    {
        lock (_sync)
        {
            int iterations = 0;
            int maxIterations = _totalFrames * 4;

            while (iterations < maxIterations)
            {
                BufferFrame frame = frames[_handCold];
                _handCold = (_handCold + 1) % _totalFrames;
                iterations++;

                if (frame.Status == PageStatus.Empty)
                {
                    return frame.FrameIndex;
                }

                if (frame.Status == PageStatus.Cold)
                {
                    if (frame.RefBit == 1)
                    {
                        // Promote to Hot
                        frame.Status = PageStatus.Hot;
                        frame.RefBit = 0;
                        _coldCount--;
                        _hotCount++;

                        // Rebalance hot pages if needed
                        AdvanceHotHand(frames);
                    }
                    else
                    {
                        // Cold page with RefBit == 0 is candidate for eviction
                        if (frame.PinCount == 0)
                        {
                            return frame.FrameIndex;
                        }
                    }
                }
                else if (frame.Status == PageStatus.Hot)
                {
                    if (frame.RefBit == 1)
                    {
                        frame.RefBit = 0;
                    }
                    else
                    {
                        // Hot page with RefBit == 0 demoted to Cold
                        frame.Status = PageStatus.Cold;
                        _hotCount--;
                        _coldCount++;
                    }
                }
            }

            // Fallback: find any unpinned frame
            for (int i = 0; i < frames.Length; i++)
            {
                if (frames[i].PinCount == 0)
                {
                    return i;
                }
            }

            return -1; // All frames pinned
        }
    }

    private void AdvanceHotHand(BufferFrame[] frames)
    {
        while (_hotCount > _totalFrames - _targetCold)
        {
            BufferFrame frame = frames[_handHot];
            _handHot = (_handHot + 1) % _totalFrames;

            if (frame.Status == PageStatus.Hot)
            {
                if (frame.RefBit == 1)
                {
                    frame.RefBit = 0;
                }
                else
                {
                    frame.Status = PageStatus.Cold;
                    _hotCount--;
                    _coldCount++;
                }
            }
        }
    }

    public void OnEvict(BufferFrame frame)
    {
        lock (_sync)
        {
            if (frame.Status == PageStatus.Cold)
            {
                _coldCount = Math.Max(0, _coldCount - 1);
                // Add evicted cold page to test history
                if (_testPages.Count >= _maxTestPages && _testQueue.Count > 0)
                {
                    PageId expired = _testQueue.Dequeue();
                    _testPages.Remove(expired);
                }
                if (_testPages.Add(frame.PageId))
                {
                    _testQueue.Enqueue(frame.PageId);
                }
            }
            else if (frame.Status == PageStatus.Hot)
            {
                _hotCount = Math.Max(0, _hotCount - 1);
            }

            frame.Status = PageStatus.Empty;
            frame.RefBit = 0;
        }
    }
}

/// <summary>
/// 2Q adaptive page replacement policy.
/// Employs A1in (FIFO), A1out (non-resident history), and Am (LRU) queues.
/// </summary>
public sealed class TwoQueuePolicy : IPageReplacementPolicy
{
    private readonly int _totalFrames;
    private readonly int _kinLimit;
    private readonly int _koutLimit;

    private readonly Queue<int> _a1In = new();
    private readonly HashSet<PageId> _a1Out = new();
    private readonly Queue<PageId> _a1OutQueue = new();
    private readonly LinkedList<int> _am = new();
    private readonly Dictionary<int, LinkedListNode<int>> _amNodes = new();
    private readonly object _sync = new();

    public TwoQueuePolicy(int totalFrames)
    {
        _totalFrames = Math.Max(1, totalFrames);
        _kinLimit = Math.Max(1, _totalFrames / 4);
        _koutLimit = Math.Max(1, _totalFrames / 2);
    }

    public void RecordAccess(BufferFrame frame)
    {
        lock (_sync)
        {
            int idx = frame.FrameIndex;
            if (_amNodes.TryGetValue(idx, out var node))
            {
                // In Am: move to front (MRU)
                _am.Remove(node);
                _am.AddFirst(node);
                return;
            }

            if (frame.Status == PageStatus.Empty)
            {
                // Check if page was in A1out
                if (_a1Out.Remove(frame.PageId))
                {
                    // Promoted directly to Am
                    frame.Status = PageStatus.Hot;
                    var amNode = _am.AddFirst(idx);
                    _amNodes[idx] = amNode;
                }
                else
                {
                    // Enter A1in
                    frame.Status = PageStatus.Cold;
                    _a1In.Enqueue(idx);
                }
            }
        }
    }

    public int SelectVictim(BufferFrame[] frames)
    {
        lock (_sync)
        {
            // If A1in size > kinLimit, evict from A1in head
            if (_a1In.Count >= _kinLimit)
            {
                int count = _a1In.Count;
                for (int i = 0; i < count; i++)
                {
                    int candidate = _a1In.Dequeue();
                    if (frames[candidate].PinCount == 0)
                    {
                        return candidate;
                    }
                    _a1In.Enqueue(candidate);
                }
            }

            // Otherwise, evict from tail of Am
            var curr = _am.Last;
            while (curr != null)
            {
                int candidate = curr.Value;
                if (frames[candidate].PinCount == 0)
                {
                    return candidate;
                }
                curr = curr.Previous;
            }

            // Fallback to any in A1in
            while (_a1In.Count > 0)
            {
                int candidate = _a1In.Dequeue();
                if (frames[candidate].PinCount == 0)
                {
                    return candidate;
                }
            }

            // Fallback: check all frames
            for (int i = 0; i < frames.Length; i++)
            {
                if (frames[i].PinCount == 0)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public void OnEvict(BufferFrame frame)
    {
        lock (_sync)
        {
            int idx = frame.FrameIndex;
            if (_amNodes.Remove(idx, out var node))
            {
                _am.Remove(node);
            }
            else
            {
                // Evicted from A1in -> add to A1out history
                if (_a1Out.Count >= _koutLimit && _a1OutQueue.Count > 0)
                {
                    PageId expired = _a1OutQueue.Dequeue();
                    _a1Out.Remove(expired);
                }
                if (_a1Out.Add(frame.PageId))
                {
                    _a1OutQueue.Enqueue(frame.PageId);
                }
            }

            frame.Status = PageStatus.Empty;
        }
    }
}
