namespace Hakobune.Presentation.Transfers;

/// <summary>
/// 直近数秒の進み具合から、転送の速さを求める。進みが止まれば速さも下がっていく。
/// </summary>
internal sealed class RateMeter(TimeProvider timeProvider)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();
    private long _lastBytes = -1;

    /// <summary>ある時点（<see cref="TimeProvider.GetTimestamp"/>）までに送った量を加える。</summary>
    public void Add(long timestamp, long bytes)
    {
        if (bytes < _lastBytes)
            _samples.Clear(); // 最初から送り直している
        _lastBytes = bytes;
        _samples.Enqueue((timestamp, bytes));
        while (_samples.Count > 2 && timeProvider.GetElapsedTime(_samples.Peek().Timestamp, timestamp) > Window)
            _samples.Dequeue();
    }

    /// <summary>いまの速さ（バイト/秒）。</summary>
    public double GetBytesPerSecond(long now)
    {
        if (_samples.Count < 2)
            return 0;
        var first = _samples.Peek();
        var seconds = timeProvider.GetElapsedTime(first.Timestamp, now).TotalSeconds;
        if (seconds <= 0.2)
            return 0;
        return Math.Max(0, (_lastBytes - first.Bytes) / seconds);
    }

    /// <summary>記録を消す。</summary>
    public void Reset()
    {
        _samples.Clear();
        _lastBytes = -1;
    }
}
