using System.Collections.Concurrent;
using Hakobune.Core.Remote;

namespace Hakobune.Core.Transfers;

/// <summary>
/// 送信待ちを管理し、決まった数の接続で順番に転送する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>接続は転送を担当する作業ごとに 1 本ずつ持ち、画面用の接続とも、ほかの作業とも共有しない。</item>
/// <item>一時的な失敗のあとはその接続を捨てて、つなぎ直す。</item>
/// <item>一時的な失敗は <see cref="TransferOptions.RetryPolicy"/> に従い、その項目のまま間隔をあけてやり直す。
/// 待ち行列の最後には回さず、回数にも上限がある。</item>
/// <item>直らない失敗はやり直さず、理由と一緒に「失敗」にする。ほかの項目の転送は止めない。</item>
/// <item>サーバーに同時接続数の上限で断られたら、同時に転送する数を減らす。</item>
/// <item>そもそも接続できないときは項目の失敗に数えず、上限まで待ってから一時停止する。</item>
/// </list>
/// </remarks>
public sealed class TransferScheduler : ITransferSink
{
    private static readonly TimeSpan ProgressEventInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly List<TransferItem> _items = [];

    // 転送中の項目を 1 件だけ止めるためのもの（項目の ID ごと）
    private readonly Dictionary<Guid, CancellationTokenSource> _itemCancellations = [];
    private readonly ConcurrentDictionary<string, byte> _createdDirectories = new(StringComparer.Ordinal);
    private readonly IRemoteSessionFactory _sessionFactory;
    private readonly TransferOptions _options;
    private readonly TimeProvider _time;
    private readonly TransferExecutor _executor;

    private TaskCompletionSource _changed = NewSignal();
    private long _nextSequence;
    private bool _running;
    private int _activeWorkers;
    private TransferFailure? _pauseReason;

    public TransferScheduler(IRemoteSessionFactory sessionFactory, TransferOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        _sessionFactory = sessionFactory;
        _options = options ?? new TransferOptions();
        _time = timeProvider ?? TimeProvider.System;
        _executor = new TransferExecutor(_options);
    }

    /// <summary>項目の状態や進み具合が変わったときに呼ばれる（どのスレッドからも呼ばれうる）。</summary>
    public event EventHandler<TransferItemSnapshot>? ItemChanged;

    /// <summary>項目を一覧から消したときに呼ばれる（どのスレッドからも呼ばれうる）。</summary>
    public event EventHandler<IReadOnlyList<Guid>>? ItemsRemoved;

    /// <summary>転送の設定。</summary>
    public TransferOptions Options => _options;

    /// <summary>送信待ちに加える。実行中に加えてもよい。</summary>
    public IReadOnlyList<TransferItemSnapshot> Enqueue(IEnumerable<TransferRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var added = new List<TransferItemSnapshot>();
        lock (_gate)
        {
            foreach (var request in requests)
            {
                ArgumentNullException.ThrowIfNull(request);
                var item = new TransferItem(_nextSequence++, request);
                _items.Add(item);
                added.Add(item.ToSnapshot());
            }
            SignalLocked();
        }
        foreach (var snapshot in added)
            Raise(snapshot);
        return added;
    }

    /// <summary>すべての項目の、いまの様子。</summary>
    public IReadOnlyList<TransferItemSnapshot> GetItems()
    {
        lock (_gate)
            return _items.Select(item => item.ToSnapshot()).ToList();
    }

    /// <summary>いまの件数。</summary>
    public TransferSummary GetSummary()
    {
        lock (_gate)
            return SummarizeLocked();
    }

    /// <summary>保存用に、すべての項目の状態を取り出す。</summary>
    public IReadOnlyList<SavedTransferItem> SaveState()
    {
        lock (_gate)
        {
            return _items.Select(item => new SavedTransferItem(
                item.Id,
                item.Request.Direction,
                item.Request.LocalPath,
                item.Request.RemotePath,
                item.Request.OnConflict,
                item.State,
                item.FailedAttempts,
                item.LastFailure?.Reason,
                item.LastFailure?.Kind,
                item.LastFailure?.ReplyCode,
                item.LastFailure?.ServerMessage,
                item.HasWrittenTarget,
                item.TotalBytes,
                item.TransferredBytes,
                item.Note,
                item.SizeVerified,
                item.FinishedAt,
                item.Request.IsFolder,
                item.Request.IsDelete,
                item.Request.Batch)).ToList();
        }
    }

    /// <summary>
    /// 保存しておいた項目を戻す。転送中だった項目は、続きから送れるように待機中に戻す。実行中は呼べない。
    /// </summary>
    public void Restore(IEnumerable<SavedTransferItem> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var restored = new List<TransferItemSnapshot>();
        lock (_gate)
        {
            if (_running)
                throw new InvalidOperationException("転送の実行中は復元できません。");

            foreach (var entry in saved)
            {
                var request = new TransferRequest(
                    entry.Direction, entry.LocalPath, entry.RemotePath, entry.OnConflict,
                    IsFolder: entry.IsFolder, IsDelete: entry.IsDelete, Batch: entry.Batch);
                var item = new TransferItem(_nextSequence++, request)
                {
                    Id = entry.Id,
                    State = entry.State switch
                    {
                        TransferState.Running when entry.FailedAttempts > 0 => TransferState.WaitingToRetry,
                        TransferState.Running => TransferState.Queued,
                        _ => entry.State,
                    },
                    FailedAttempts = entry.FailedAttempts,
                    LastFailure = entry.LastFailureReason is { } reason
                        ? new TransferFailure(reason, entry.LastFailureKind ?? FailureKind.Permanent, entry.LastFailureCode, entry.LastFailureMessage)
                        : null,
                    HasWrittenTarget = entry.HasWrittenTarget,
                    TotalBytes = entry.TotalBytes,
                    TransferredBytes = entry.TransferredBytes,
                    Note = entry.Note,
                    SizeVerified = entry.SizeVerified,
                    FinishedAt = entry.FinishedAt,
                };
                _items.Add(item);
                restored.Add(item.ToSnapshot());
            }
            SignalLocked();
        }
        foreach (var snapshot in restored)
            Raise(snapshot);
    }

    /// <summary>失敗した項目を待機中に戻す（「失敗分を再送」）。戻した件数を返す。</summary>
    public int RetryFailed()
    {
        var changed = new List<TransferItemSnapshot>();
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (item.State != TransferState.Failed)
                    continue;
                ResetLocked(item);
                changed.Add(item.ToSnapshot());
            }
            SignalLocked();
        }
        foreach (var snapshot in changed)
            Raise(snapshot);
        return changed.Count;
    }

    /// <summary>
    /// 項目を取り消す。待機中ならすぐに、転送中なら止まったところで「取り消し」にする。終わった項目はそのまま。
    /// 取り消す項目の数を返す。
    /// </summary>
    public int Cancel(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.ToHashSet();
        var changed = new List<TransferItemSnapshot>();
        var toStop = new List<CancellationTokenSource>();
        var count = 0;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var item in _items)
            {
                if (!wanted.Contains(item.Id))
                    continue;
                switch (item.State)
                {
                    case TransferState.Queued or TransferState.WaitingToRetry:
                        item.State = TransferState.Cancelled;
                        item.RetryAt = null;
                        item.FinishedAt = now;
                        changed.Add(item.ToSnapshot());
                        count++;
                        break;
                    case TransferState.Running:
                        item.CancelRequested = true;
                        if (_itemCancellations.TryGetValue(item.Id, out var cancellation))
                            toStop.Add(cancellation);
                        count++;
                        break;
                }
            }
            SignalLocked();
        }

        // 止める合図はロックの外で出す（合図を受けた処理がその場で動くことがある）
        foreach (var cancellation in toStop)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ちょうど終わったところ
            }
        }
        foreach (var snapshot in changed)
            Raise(snapshot);
        return count;
    }

    /// <summary>失敗・取り消しの項目を待機中に戻す（1 件ずつの「再送」）。戻した件数を返す。</summary>
    public int Retry(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.ToHashSet();
        var changed = new List<TransferItemSnapshot>();
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (!wanted.Contains(item.Id) || item.State is not (TransferState.Failed or TransferState.Cancelled))
                    continue;
                ResetLocked(item);
                changed.Add(item.ToSnapshot());
            }
            SignalLocked();
        }
        foreach (var snapshot in changed)
            Raise(snapshot);
        return changed.Count;
    }

    /// <summary>やり直しを待っている項目を、待たずにすぐやり直す（「今すぐ再試行」）。対象の件数を返す。</summary>
    public int RetryNow(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.ToHashSet();
        var changed = new List<TransferItemSnapshot>();
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (!wanted.Contains(item.Id) || item.State != TransferState.WaitingToRetry)
                    continue;
                item.RetryAt = null;
                changed.Add(item.ToSnapshot());
            }
            SignalLocked();
        }
        foreach (var snapshot in changed)
            Raise(snapshot);
        return changed.Count;
    }

    /// <summary>転送中でない項目を一覧から消す。消した件数を返す。</summary>
    public int Remove(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.ToHashSet();
        return RemoveWhere(item => wanted.Contains(item.Id) && item.State != TransferState.Running);
    }

    /// <summary>終わった項目（完了・スキップ・取り消し）を一覧から消す。消した件数を返す。</summary>
    public int RemoveFinished() =>
        RemoveWhere(item => item.State is TransferState.Completed or TransferState.Skipped or TransferState.Cancelled);

    private int RemoveWhere(Predicate<TransferItem> match)
    {
        List<Guid> removed;
        lock (_gate)
        {
            removed = [.. _items.Where(item => match(item)).Select(item => item.Id)];
            if (removed.Count == 0)
                return 0;
            _items.RemoveAll(match);
            SignalLocked();
        }
        ItemsRemoved?.Invoke(this, removed);
        return removed.Count;
    }

    /// <summary>
    /// 待機中の項目がなくなるまで転送する。一時停止や中止のあとに呼べば、残りを続きから転送する。
    /// </summary>
    public async Task<TransferRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        int workerCount;
        lock (_gate)
        {
            if (_running)
                throw new InvalidOperationException("転送はすでに実行中です。");
            _running = true;
            _pauseReason = null;
            workerCount = Math.Max(1, _options.MaxConcurrency);
            _activeWorkers = workerCount;
        }

        try
        {
            var workers = new Task[workerCount];
            for (var i = 0; i < workerCount; i++)
                workers[i] = Task.Run(() => WorkerAsync(cancellationToken), CancellationToken.None);
            await Task.WhenAll(workers).ConfigureAwait(false);

            lock (_gate)
            {
                var summary = SummarizeLocked();
                if (cancellationToken.IsCancellationRequested)
                    return new TransferRunResult(TransferRunOutcome.Cancelled, summary);
                if (_pauseReason is { } reason)
                    return new TransferRunResult(TransferRunOutcome.Paused, summary, reason);
                return new TransferRunResult(TransferRunOutcome.Finished, summary);
            }
        }
        finally
        {
            lock (_gate)
                _running = false;
        }
    }

    // 転送を担当する作業 1 つ分。接続を 1 本だけ持ち、ほかとは共有しない。
    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        IRemoteSession? session = null;
        var retired = false;
        try
        {
            while (true)
            {
                var item = await TakeNextAsync(cancellationToken).ConfigureAwait(false);
                if (item is null)
                    return;

                if (session is null || !session.IsConnected)
                {
                    await DisposeQuietlyAsync(session).ConfigureAwait(false);
                    session = null;

                    var (connected, failure, leftToOthers) = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                    if (connected is null)
                    {
                        Release(item);
                        if (failure is null)
                            return; // 中止された
                        if (leftToOthers)
                        {
                            // サーバーの同時接続数の上限。この作業はやめて、残りの接続で続ける。
                            if (TryRetire())
                            {
                                retired = true;
                                return;
                            }
                            // そのあいだに、ほかの作業が全部終わっていた。止めずに、この作業で続ける
                            // （ほかの接続が閉じたので、次はつながるはず。だめなら、ふつうのやり直しの回数で止まる）
                            continue;
                        }
                        Pause(failure);
                        return;
                    }
                    session = connected;
                }

                var sessionIsUsable = await ExecuteAsync(item, session, cancellationToken).ConfigureAwait(false);
                if (!sessionIsUsable)
                {
                    await DisposeQuietlyAsync(session).ConfigureAwait(false);
                    session = null;
                }
                if (cancellationToken.IsCancellationRequested)
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 中止された。転送中だった項目は ExecuteAsync で待機中に戻してある。
        }
        finally
        {
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
            lock (_gate)
            {
                if (!retired)
                    _activeWorkers--;
                SignalLocked();
            }
        }
    }

    // 1 件を実行する。戻り値は、この接続をこのあとも使ってよいか。
    private async Task<bool> ExecuteAsync(TransferItem item, IRemoteSession session, CancellationToken cancellationToken)
    {
        using var itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _itemCancellations[item.Id] = itemCancellation;
            if (item.CancelRequested)
                itemCancellation.Cancel(); // 接続を待つあいだに取り消された
        }
        try
        {
            var result = await _executor.ExecuteAsync(item, session, this, itemCancellation.Token).ConfigureAwait(false);
            Finish(item, result);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Release(item);
            return false;
        }
        catch (Exception) when (IsCancelRequested(item))
        {
            // 取り消された。途中で止めた接続は、このあと使わない
            MarkCancelled(item);
            return false;
        }
        catch (Exception ex)
        {
            var failure = FailureClassifier.FromException(ex);
            Fail(item, failure);

            // 一時的な失敗や想定外のエラーのあとは、その接続を信用しない（FFFTP の「壊れた接続の使い回し」を防ぐ）。
            return !failure.IsTransient && failure.Reason != FailureReason.Unknown;
        }
        finally
        {
            lock (_gate)
                _itemCancellations.Remove(item.Id);
        }
    }

    private bool IsCancelRequested(TransferItem item)
    {
        lock (_gate)
            return item.CancelRequested;
    }

    private void MarkCancelled(TransferItem item)
    {
        TransferItemSnapshot snapshot;
        lock (_gate)
        {
            item.State = TransferState.Cancelled;
            item.CancelRequested = false;
            item.RetryAt = null;
            item.FinishedAt = _time.GetUtcNow();
            snapshot = item.ToSnapshot();
            SignalLocked();
        }
        Raise(snapshot);
    }

    // やり直しの前の状態に戻す（ロックの中で呼ぶ）
    private static void ResetLocked(TransferItem item)
    {
        item.State = TransferState.Queued;
        item.FailedAttempts = 0;
        item.LastFailure = null;
        item.RetryAt = null;
        item.Note = null;
        item.FinishedAt = null;
        item.CancelRequested = false;
    }

    // つなぐ。つなげなければ理由を返す（中止なら理由も null）。
    // LeftToOthers は、サーバーの同時接続数の上限に当たり、ほかの作業がつないでいるので、やり直さずに譲ったこと。
    private async Task<(IRemoteSession? Session, TransferFailure? Failure, bool LeftToOthers)> ConnectAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var session = _sessionFactory.Create();
            try
            {
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return (session, null, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeQuietlyAsync(session).ConfigureAwait(false);
                return (null, null, false);
            }
            catch (Exception ex)
            {
                await DisposeQuietlyAsync(session).ConfigureAwait(false);

                var failure = FailureClassifier.FromException(ex);
                if (failure.Reason == FailureReason.TooManyConnections && HasOtherActiveWorkers())
                    return (null, failure, true);
                if (!_options.ConnectPolicy.ShouldRetry(failure, attempt))
                    return (null, failure, false);

                try
                {
                    await Task.Delay(_options.ConnectPolicy.GetDelay(attempt), _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return (null, null, false);
                }
            }
        }
    }

    // 次に転送する項目を選んで「転送中」にする。すべて終わっていれば null。
    private async Task<TransferItem?> TakeNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TransferItem? ready;
            TransferItemSnapshot? snapshot = null;
            var changed = Task.CompletedTask;
            TimeSpan? delay = null;

            lock (_gate)
            {
                if (_pauseReason is not null || cancellationToken.IsCancellationRequested)
                    return null;

                var now = _time.GetUtcNow();
                ready = FindReadyLocked(now, out var pending, out var nextRetry);
                if (ready is not null)
                {
                    ready.State = TransferState.Running;
                    ready.RetryAt = null;
                    ready.Note = null;
                    snapshot = ready.ToSnapshot();
                }
                else
                {
                    if (!pending)
                        return null;
                    changed = _changed.Task;
                    if (nextRetry is { } at)
                        delay = at - now;
                }
            }

            if (ready is not null && snapshot is not null)
            {
                Raise(snapshot);
                return ready;
            }

            await WaitAsync(changed, delay, cancellationToken).ConfigureAwait(false);
        }
    }

    // 並び順で最初の「待機中」か「やり直しの時刻が来た」項目を探す。
    // やり直しの項目は元の位置のままなので、時刻が来れば後ろの項目より先に選ばれる。
    private TransferItem? FindReadyLocked(DateTimeOffset now, out bool pending, out DateTimeOffset? nextRetry)
    {
        pending = false;
        nextRetry = null;
        foreach (var item in _items)
        {
            switch (item.State)
            {
                case TransferState.Queued:
                    return item;

                case TransferState.WaitingToRetry:
                {
                    var retryAt = item.RetryAt ?? DateTimeOffset.MinValue;
                    if (retryAt <= now)
                        return item;
                    pending = true;
                    if (nextRetry is null || retryAt < nextRetry.Value)
                        nextRetry = retryAt;
                    break;
                }

                case TransferState.Running:
                    pending = true;
                    break;
            }
        }
        return null;
    }

    private async Task WaitAsync(Task changed, TimeSpan? delay, CancellationToken cancellationToken)
    {
        if (delay is { } due && due <= TimeSpan.Zero)
            return;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Task.Delay(delay ?? Timeout.InfiniteTimeSpan, _time, timeoutSource.Token);
        await Task.WhenAny(changed, timeout).ConfigureAwait(false);
        await timeoutSource.CancelAsync().ConfigureAwait(false);
    }

    private void Finish(TransferItem item, ExecutionResult result)
    {
        TransferItemSnapshot snapshot;
        lock (_gate)
        {
            item.State = result.State;
            item.Note = result.Note;
            item.SizeVerified = result.SizeVerified;
            item.CancelRequested = false; // 取り消しが間に合わなかった（もう終わっていた）
            item.RetryAt = null;
            item.FinishedAt = _time.GetUtcNow();
            if (result.State == TransferState.Completed)
                item.TransferredBytes = item.TotalBytes;
            snapshot = item.ToSnapshot();
            SignalLocked();
        }
        Raise(snapshot);
    }

    private void Fail(TransferItem item, TransferFailure failure)
    {
        TransferItemSnapshot snapshot;
        lock (_gate)
        {
            item.FailedAttempts++;
            item.LastFailure = failure;
            if (_options.RetryPolicy.ShouldRetry(failure, item.FailedAttempts))
            {
                item.State = TransferState.WaitingToRetry;
                item.RetryAt = _time.GetUtcNow() + _options.RetryPolicy.GetDelay(item.FailedAttempts);
            }
            else
            {
                item.State = TransferState.Failed;
                item.RetryAt = null;
                item.FinishedAt = _time.GetUtcNow();
            }
            snapshot = item.ToSnapshot();
            SignalLocked();
        }
        Raise(snapshot);
    }

    // 試行を始めずに手放す（接続できない、中止された）。失敗の回数には数えない。
    private void Release(TransferItem item)
    {
        TransferItemSnapshot snapshot;
        lock (_gate)
        {
            if (item.State == TransferState.Running)
            {
                if (item.CancelRequested)
                {
                    item.State = TransferState.Cancelled;
                    item.CancelRequested = false;
                    item.FinishedAt = _time.GetUtcNow();
                }
                else
                {
                    item.State = item.FailedAttempts > 0 ? TransferState.WaitingToRetry : TransferState.Queued;
                }
                item.RetryAt = null;
            }
            snapshot = item.ToSnapshot();
            SignalLocked();
        }
        Raise(snapshot);
    }

    private void Pause(TransferFailure failure)
    {
        lock (_gate)
        {
            _pauseReason ??= failure;
            SignalLocked();
        }
    }

    private bool TryRetire()
    {
        lock (_gate)
        {
            if (_activeWorkers <= 1)
                return false;
            _activeWorkers--;
            SignalLocked();
            return true;
        }
    }

    private bool HasOtherActiveWorkers()
    {
        lock (_gate)
            return _activeWorkers > 1;
    }

    private TransferSummary SummarizeLocked()
    {
        int queued = 0, running = 0, waiting = 0, completed = 0, failed = 0, skipped = 0, cancelled = 0;
        foreach (var item in _items)
        {
            switch (item.State)
            {
                case TransferState.Queued:
                    queued++;
                    break;
                case TransferState.Running:
                    running++;
                    break;
                case TransferState.WaitingToRetry:
                    waiting++;
                    break;
                case TransferState.Completed:
                    completed++;
                    break;
                case TransferState.Failed:
                    failed++;
                    break;
                case TransferState.Skipped:
                    skipped++;
                    break;
                case TransferState.Cancelled:
                    cancelled++;
                    break;
            }
        }
        return new TransferSummary(_items.Count, queued, running, waiting, completed, failed, skipped, cancelled);
    }

    // 待っている作業をすべて起こす。
    private void SignalLocked()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Raise(TransferItemSnapshot snapshot) => ItemChanged?.Invoke(this, snapshot);

    private static async Task DisposeQuietlyAsync(IRemoteSession? session)
    {
        if (session is null)
            return;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 切れた接続の後片付けに失敗しても、転送には影響しない。
        }
    }

    void ITransferSink.SetTotalBytes(TransferItem item, long totalBytes)
    {
        TransferItemSnapshot snapshot;
        lock (_gate)
        {
            item.TotalBytes = totalBytes;
            snapshot = item.ToSnapshot();
        }
        Raise(snapshot);
    }

    void ITransferSink.MarkTargetWritten(TransferItem item)
    {
        lock (_gate)
            item.HasWrittenTarget = true;
    }

    void ITransferSink.ReportProgress(TransferItem item, long transferredBytes)
    {
        TransferItemSnapshot? snapshot = null;
        lock (_gate)
        {
            item.TransferredBytes = transferredBytes;
            var now = _time.GetTimestamp();
            var due = item.LastProgressTimestamp == 0
                || _time.GetElapsedTime(item.LastProgressTimestamp, now) >= ProgressEventInterval
                || transferredBytes >= item.TotalBytes;
            if (due)
            {
                item.LastProgressTimestamp = now;
                snapshot = item.ToSnapshot();
            }
        }
        if (snapshot is not null)
            Raise(snapshot);
    }

    async Task ITransferSink.EnsureRemoteDirectoryAsync(IRemoteSession session, string directory, CancellationToken cancellationToken)
    {
        if (directory is "" or "/" || _createdDirectories.ContainsKey(directory))
            return;
        await session.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        _createdDirectories.TryAdd(directory, 0);
    }

    // 消したフォルダ（とその中）は、作ってあるとは限らなくなるので忘れる
    void ITransferSink.ForgetRemoteDirectory(string path)
    {
        var prefix = path.TrimEnd('/') + "/";
        foreach (var directory in _createdDirectories.Keys)
        {
            if (directory == path || directory.StartsWith(prefix, StringComparison.Ordinal))
                _createdDirectories.TryRemove(directory, out _);
        }
    }
}
