namespace BlackBox.Core;

/// <summary>
/// 시간 기반 링버퍼. 메모리에만 유지하고 디스크에 쓰지 않는다.
/// 보존 기간은 "마킹 전 구간(pre) + 마킹 후 구간(post) + 여유"로 잡아야
/// T+post 시점 덤프에서 T-pre 이벤트가 이미 밀려나지 않는다.
/// </summary>
public sealed class RingBuffer
{
    private readonly object _lock = new();
    private readonly Queue<SignalEvent> _events = new();
    private readonly TimeSpan _retention;
    private long _approxBytes;

    public RingBuffer(TimeSpan retention) => _retention = retention;

    public void Append(SignalEvent e)
    {
        lock (_lock)
        {
            _events.Enqueue(e);
            _approxBytes += e.ApproxBytes;
            TrimLocked(e.Timestamp);
        }
    }

    public IReadOnlyList<SignalEvent> Snapshot(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            return _events.Where(e => e.Timestamp >= from && e.Timestamp <= to).ToList();
        }
    }

    public (int Count, long ApproxBytes) Stats
    {
        get
        {
            lock (_lock)
            {
                TrimLocked(DateTimeOffset.Now);
                return (_events.Count, _approxBytes);
            }
        }
    }

    /// <summary>소스별 (건수, 근사 바이트) 내역 — ETW 등 개별 소스의 메모리 기여분 측정용.</summary>
    public Dictionary<string, (int Count, long Bytes)> StatsBySource()
    {
        lock (_lock)
        {
            TrimLocked(DateTimeOffset.Now);
            return _events.GroupBy(e => e.Source)
                .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(e => (long)e.ApproxBytes)));
        }
    }

    private void TrimLocked(DateTimeOffset now)
    {
        var cutoff = now - _retention;
        while (_events.Count > 0 && _events.Peek().Timestamp < cutoff)
            _approxBytes -= _events.Dequeue().ApproxBytes;
    }
}
