using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>
/// 接続先 1 つ分の送信待ちを動かす。加えるとすぐに転送を始め、終わったら止まる。
/// 転送は自分の接続で行い、画面用の接続は使わない。送信待ちはファイルに残し、次に接続したときに続けられる。
/// </summary>
public sealed class TransferController : ITransferSource, IAsyncDisposable
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly TransferScheduler _scheduler;
    private readonly string? _queueFile;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ITimer _saveTimer;
    private CancellationTokenSource? _run;
    private Task _runTask = Task.CompletedTask;
    private bool _paused;
    private bool _disposed;
    private int _dirty;

    /// <summary>
    /// <paramref name="queueFile"/> に送信待ちを保存する（null なら保存しない。クイック接続など）。
    /// </summary>
    public TransferController(IRemoteSessionFactory sessionFactory, TransferOptions? options = null, string? queueFile = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        var time = timeProvider ?? TimeProvider.System;
        _scheduler = new TransferScheduler(sessionFactory, options, time);
        _queueFile = queueFile;
        _scheduler.ItemChanged += (_, _) => Interlocked.Exchange(ref _dirty, 1);
        _scheduler.ItemsRemoved += (_, _) => Interlocked.Exchange(ref _dirty, 1);
        // 転送のあいだも、ときどき保存しておく（アプリが落ちても送信待ちが残るように）
        _saveTimer = time.CreateTimer(_ => _ = SaveIfChangedAsync(), null, SaveInterval, SaveInterval);
    }

    /// <inheritdoc />
    public event EventHandler<TransferItemSnapshot>? ItemChanged
    {
        add => _scheduler.ItemChanged += value;
        remove => _scheduler.ItemChanged -= value;
    }

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<Guid>>? ItemsRemoved
    {
        add => _scheduler.ItemsRemoved += value;
        remove => _scheduler.ItemsRemoved -= value;
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <summary>項目の状態が変わったときにも呼ばれる、送信待ちそのもの（試験用）。</summary>
    internal TransferScheduler Scheduler => _scheduler;

    /// <inheritdoc />
    public int MaxAttempts => _scheduler.Options.RetryPolicy.MaxAttempts;

    /// <inheritdoc />
    public int MaxConcurrency => _scheduler.Options.MaxConcurrency;

    /// <inheritdoc />
    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _paused;
        }
    }

    /// <inheritdoc />
    public TransferFailure? StopReason
    {
        get
        {
            lock (_gate)
                return field;
        }
        private set
        {
            lock (_gate)
                field = value;
        }
    }

    /// <summary>転送しているところか。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _run is not null;
        }
    }

    /// <inheritdoc />
    public int RestoredPendingCount
    {
        get
        {
            lock (_gate)
                return field;
        }
        private set
        {
            lock (_gate)
                field = value;
        }
    }

    /// <summary>
    /// 保存しておいた送信待ちを読み込む。終わっていない項目があれば、勝手には始めず一時停止にしておく。
    /// 完了した項目は読み込まない。
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_queueFile is null)
            return;
        IReadOnlyList<SavedTransferItem> saved;
        try
        {
            saved = await TransferQueueFile.LoadAsync(_queueFile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // 読めなければ空から始める（ファイルは次に保存するときに置き換わる）
        }

        var kept = saved.Where(item => item.State is not (TransferState.Completed or TransferState.Skipped or TransferState.Cancelled)).ToList();
        if (kept.Count == 0)
            return;
        _scheduler.Restore(kept);
        RestoredPendingCount = _scheduler.GetSummary().Pending;
        if (RestoredPendingCount > 0)
        {
            lock (_gate)
                _paused = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>送信待ちに加え、止めていなければすぐに転送を始める。</summary>
    public IReadOnlyList<TransferItemSnapshot> Enqueue(IEnumerable<TransferRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var added = _scheduler.Enqueue(requests);
        Interlocked.Exchange(ref _dirty, 1);
        EnsureRunning();
        return added;
    }

    /// <summary>まだ終わっていない件数。</summary>
    public int PendingCount => _scheduler.GetSummary().Pending;

    /// <inheritdoc />
    public IReadOnlyList<TransferItemSnapshot> GetItems() => _scheduler.GetItems();

    /// <inheritdoc />
    public void Pause()
    {
        CancellationTokenSource? run;
        lock (_gate)
        {
            if (_paused)
                return;
            _paused = true;
            run = _run;
        }
        Cancel(run);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = SaveIfChangedAsync();
    }

    /// <inheritdoc />
    public void Resume()
    {
        lock (_gate)
            _paused = false;
        StopReason = null;
        RestoredPendingCount = 0;
        StateChanged?.Invoke(this, EventArgs.Empty);
        EnsureRunning();
    }

    /// <inheritdoc />
    public int RetryFailed() => AndRun(_scheduler.RetryFailed());

    /// <inheritdoc />
    public int Retry(IEnumerable<Guid> ids) => AndRun(_scheduler.Retry(ids));

    /// <inheritdoc />
    public int RetryNow(IEnumerable<Guid> ids) => AndRun(_scheduler.RetryNow(ids));

    /// <inheritdoc />
    public int Cancel(IEnumerable<Guid> ids) => _scheduler.Cancel(ids);

    /// <inheritdoc />
    public int RemoveFinished() => _scheduler.RemoveFinished();

    /// <summary>保存する（変わっていなくても）。</summary>
    public Task SaveAsync() => SaveCoreAsync(force: true);

    /// <summary>転送を止め（項目は待機中に戻す）、送信待ちを保存する。</summary>
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? run;
        Task runTask;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            run = _run;
            runTask = _runTask;
        }
        await _saveTimer.DisposeAsync().ConfigureAwait(false);
        Cancel(run);
        try
        {
            // 止まらない接続があっても、閉じるのを長く待たせない（転送中だった項目は、次に読み込むとき待機中に戻る）
            await runTask.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 転送の失敗は項目ごとに記録してある
        }
        await SaveCoreAsync(force: true).ConfigureAwait(false);
    }

    private int AndRun(int count)
    {
        if (count > 0)
            EnsureRunning();
        return count;
    }

    // 止めていなくて、送るものがあれば、転送を始める
    private void EnsureRunning()
    {
        lock (_gate)
        {
            if (_paused || _disposed || _run is not null || _scheduler.GetSummary().Pending == 0)
                return;
            var run = new CancellationTokenSource();
            _run = run;
            // 呼んだスレッド（画面のスレッドのこともある）で転送を進めない
            _runTask = Task.Run(() => RunAsync(run), CancellationToken.None);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(CancellationTokenSource run)
    {
        try
        {
            while (!run.IsCancellationRequested)
            {
                var result = await _scheduler.RunAsync(run.Token).ConfigureAwait(false);
                if (result.Outcome == TransferRunOutcome.Paused)
                {
                    // 接続できない。項目は待機中のまま、利用者が再開するまで止めておく
                    StopReason = result.PauseReason;
                    lock (_gate)
                        _paused = true;
                    break;
                }
                if (result.Outcome == TransferRunOutcome.Cancelled || result.Summary.Pending == 0)
                    break;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_run, run))
                    _run = null;
            }
            run.Dispose();
            StateChanged?.Invoke(this, EventArgs.Empty);
            await SaveIfChangedAsync().ConfigureAwait(false);
        }

        // 終わりかけたときに加わった項目を取りこぼさない
        EnsureRunning();
    }

    private static void Cancel(CancellationTokenSource? run)
    {
        try
        {
            run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ちょうど終わったところ
        }
    }

    private Task SaveIfChangedAsync() => SaveCoreAsync(force: false);

    private async Task SaveCoreAsync(bool force)
    {
        if (_queueFile is null)
            return;
        if (Interlocked.Exchange(ref _dirty, 0) == 0 && !force)
            return;
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var items = _scheduler.SaveState();
            if (items.Count == 0)
            {
                TryDelete(_queueFile);
                return;
            }
            await TransferQueueFile.SaveAsync(_queueFile, items).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても転送は続ける。次の機会にもう一度保存する
            Interlocked.Exchange(ref _dirty, 1);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
