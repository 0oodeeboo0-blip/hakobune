using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests;

/// <summary>時刻を自由に進められる時計。</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private long _ticks;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }
}

/// <summary>時刻を進めたときだけタイマーが動く時計（決まった時間がたったあとの動きを確かめる）。</summary>
internal sealed class SteppingTime(DateTimeOffset start) : TimeProvider
{
    private readonly List<SteppingTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SteppingTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>時刻を進め、時間になったタイマーを動かす（くり返しのタイマーは 1 回だけ動かす）。</summary>
    public void Advance(TimeSpan by)
    {
        SteppingTimer[] due;
        lock (_timers)
        {
            _now += by;
            due = [.. _timers.Where(timer => timer.DueAt <= _now)];
            foreach (var timer in due)
                _timers.Remove(timer);
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class SteppingTimer(SteppingTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._timers)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;
                DueAt = owner._now + dueTime;
                owner._timers.Add(this);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._timers)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>決まった中身を返す一覧。失敗もさせられる。</summary>
internal sealed class FakeFileSource : IFileSource
{
    public Dictionary<string, List<FileEntry>> Folders { get; } = new(StringComparer.Ordinal);

    public Func<string, TransferFailure?>? Fault { get; set; }

    public string StartPath { get; set; } = "/data";

    public int ListCalls { get; private set; }

    public Task<string> GetStartPathAsync(CancellationToken cancellationToken) => Task.FromResult(StartPath);

    public Task<FileListing> ListAsync(string path, CancellationToken cancellationToken)
    {
        ListCalls++;
        var now = DateTimeOffset.UtcNow;
        if (Fault?.Invoke(path) is { } failure)
            return Task.FromResult(new FileListing(path, [], failure, now));
        if (!Folders.TryGetValue(path, out var entries))
            return Task.FromResult(new FileListing(path, [], TransferFailure.Permanent(FailureReason.NotFound), now));
        return Task.FromResult(new FileListing(path, entries, null, now));
    }

    public string? GetParent(string path) => path is "/" or "" ? null : RemotePath.GetParent(path);

    public IReadOnlyList<PathSegment> GetSegments(string path)
    {
        var segments = new List<PathSegment> { new("server", "/") };
        var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            segments.Add(new PathSegment(part, current));
        }
        return segments;
    }

    public static FileEntry File(string folder, string name, long size, DateTimeOffset? modified = null) =>
        new(name, RemotePath.Combine(folder, name), FileEntryType.File, size, modified ?? DateTimeOffset.UtcNow);

    public static FileEntry Folder(string folder, string name) =>
        new(name, RemotePath.Combine(folder, name), FileEntryType.Directory, 0, DateTimeOffset.UtcNow);
}

/// <summary>項目を足したり書き換えたりできる送信待ち。</summary>
internal sealed class FakeTransferSource : ITransferSource
{
    private readonly List<TransferItemSnapshot> _items = [];

    public event EventHandler<TransferItemSnapshot>? ItemChanged;

    public event EventHandler<IReadOnlyList<Guid>>? ItemsRemoved;

    public event EventHandler? StateChanged;

    public int MaxAttempts => 5;

    public int MaxConcurrency => 3;

    public int RetryFailedCalls { get; private set; }

    public bool IsPaused { get; private set; }

    public TransferFailure? StopReason { get; private set; }

    public int RestoredPendingCount { get; set; }

    public List<Guid> Cancelled { get; } = [];

    public List<Guid> Retried { get; } = [];

    public List<Guid> RetriedNow { get; } = [];

    public IReadOnlyList<TransferItemSnapshot> GetItems() => [.. _items];

    public void Pause()
    {
        IsPaused = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        IsPaused = false;
        StopReason = null;
        RestoredPendingCount = 0;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>接続できなくて止まったことにする。</summary>
    public void Stop(TransferFailure reason)
    {
        IsPaused = true;
        StopReason = reason;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public int RetryFailed()
    {
        RetryFailedCalls++;
        return 0;
    }

    public int Retry(IEnumerable<Guid> ids)
    {
        Retried.AddRange(ids);
        return 0;
    }

    public int RetryNow(IEnumerable<Guid> ids)
    {
        RetriedNow.AddRange(ids);
        return 0;
    }

    public int Cancel(IEnumerable<Guid> ids)
    {
        Cancelled.AddRange(ids);
        return 0;
    }

    public int RemoveFinished()
    {
        var removed = _items.Where(item => item.State is TransferState.Completed or TransferState.Skipped or TransferState.Cancelled)
            .Select(item => item.Id)
            .ToList();
        _items.RemoveAll(item => removed.Contains(item.Id));
        if (removed.Count > 0)
            ItemsRemoved?.Invoke(this, removed);
        return removed.Count;
    }

    public TransferItemSnapshot Add(
        string name,
        TransferState state,
        long total = 1000,
        long transferred = 0,
        int failedAttempts = 0,
        TransferFailure? failure = null,
        DateTimeOffset? retryAt = null,
        DateTimeOffset? finishedAt = null,
        bool sizeVerified = false,
        TransferDirection direction = TransferDirection.Upload,
        bool isDelete = false)
    {
        var snapshot = new TransferItemSnapshot(
            Guid.NewGuid(), _items.Count, direction, isDelete ? "" : @"C:\Users\user\Sites\" + name, "/var/www/html/" + name,
            state, failedAttempts, failure, retryAt, total, transferred, null, sizeVerified, finishedAt, IsDelete: isDelete);
        _items.Add(snapshot);
        ItemChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    public void Update(TransferItemSnapshot snapshot)
    {
        var index = _items.FindIndex(item => item.Id == snapshot.Id);
        _items[index] = snapshot;
        ItemChanged?.Invoke(this, snapshot);
    }
}
