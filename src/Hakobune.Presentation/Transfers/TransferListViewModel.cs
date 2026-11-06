using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Text;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>
/// 下の転送の一覧と、ステータスバーの進み具合。
/// </summary>
/// <remarks>
/// 送信待ちの変化は別のスレッドから届くので、ためておいて画面のスレッドでまとめて反映する（0.25 秒に 1 回まで）。
/// </remarks>
public sealed class TransferListViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private readonly Dictionary<Guid, TransferRowViewModel> _rows = [];

    // 届いた変化（どの送信待ちのものかを、切り替えの回数で見分ける）
    private readonly ConcurrentQueue<(int Generation, TransferItemSnapshot Snapshot)> _pending = new();
    private readonly ConcurrentQueue<(int Generation, IReadOnlyList<Guid> Ids)> _removals = new();
    private readonly HashSet<Guid> _removedIds = [];
    private ITransferSource _source;
    private int _generation;
    private EventHandler<TransferItemSnapshot>? _onItemChanged;
    private EventHandler<IReadOnlyList<Guid>>? _onItemsRemoved;
    private EventHandler? _onStateChanged;
    private readonly Dictionary<Guid, int> _queuePositions = [];
    private readonly Lock _flushLock = new();
    private readonly ITimer? _flushTimer;
    private int _flushScheduled;
    private ITimer? _tickTimer;
    private TransferTabViewModel _selectedTab;
    private bool _disposed;

    /// <summary>
    /// <paramref name="source"/> の項目を一覧にする。変化は <paramref name="context"/>（画面のスレッド）でまとめて反映する。
    /// null なら、届いたその場で反映する（試験用）。
    /// </summary>
    public TransferListViewModel(ITransferSource source, TimeProvider? timeProvider, SynchronizationContext? context)
    {
        ArgumentNullException.ThrowIfNull(source);
        _time = timeProvider ?? TimeProvider.System;
        _context = context;

        Tabs = new List<TransferTabViewModel>
        {
            new(TransferTab.All, "すべて"),
            new(TransferTab.Active, "進行中"),
            new(TransferTab.Waiting, "待機中"),
            new(TransferTab.Done, "完了"),
            new(TransferTab.Failed, "失敗"),
        };
        _selectedTab = Tabs[0];
        _selectedTab.IsSelected = true;

        // 画面のスレッドがあるときは、変化をまとめて反映するためのタイマーを用意しておく
        if (_context is not null)
            _flushTimer = _time.CreateTimer(_ => Post(Flush), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        RetryFailedCommand = new RelayCommand(RetryFailed, () => FailedCount > 0);
        SelectTabCommand = new RelayCommand<TransferTabViewModel>(tab =>
        {
            if (tab is not null)
                SelectTab(tab.Tab);
        });
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        PauseAllCommand = new RelayCommand(TogglePause, () => PendingCount > 0 || IsPaused);
        ClearFinishedCommand = new RelayCommand(() => _source.RemoveFinished(), () => CompletedCount + CancelledCount > 0);

        _source = EmptyTransferSource.Instance;
        Attach(source);
    }

    /// <summary>タブ。</summary>
    public IReadOnlyList<TransferTabViewModel> Tabs { get; }

    /// <summary>選んでいるタブ。</summary>
    public TransferTabViewModel SelectedTab
    {
        get => _selectedTab;
        private set => SetProperty(ref _selectedTab, value);
    }

    /// <summary>選んでいるタブに出す行。</summary>
    public ObservableList<TransferRowViewModel> Rows { get; } = [];

    /// <summary>一覧を広げているか。</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(ExpandGlyphRotation));
        }
    } = true;

    /// <summary>開閉のボタンの矢印の向き（度）。</summary>
    public double ExpandGlyphRotation => IsExpanded ? 0 : 180;

    /// <summary>項目の総数。</summary>
    public int TotalCount { get; private set => SetProperty(ref field, value); }

    /// <summary>完了の数（スキップを含む）。</summary>
    public int CompletedCount
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                ClearFinishedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>取り消した数。</summary>
    public int CancelledCount
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                ClearFinishedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>一時停止しているか（利用者が止めた、または接続できなくて止まった）。</summary>
    public bool IsPaused
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value))
                return;
            PauseAllCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(PauseButtonText));
            OnPropertyChanged(nameof(PauseButtonGlyph));
        }
    }

    /// <summary>止まっている理由（例: 一時停止中、接続できないため止まっています）。止まっていなければ空。</summary>
    public string PauseText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>接続できなくて止まったか（注意の色で出す）。</summary>
    public bool IsStoppedByFailure { get; private set => SetProperty(ref field, value); }

    /// <summary>一時停止のボタンの説明。</summary>
    public string PauseButtonText => IsPaused ? "再開" : "すべて一時停止";

    /// <summary>一時停止のボタンのアイコン（再生か一時停止）。</summary>
    public string PauseButtonGlyph => IsPaused ? "\uE768" : "\uE769";

    /// <summary>失敗の数。</summary>
    public int FailedCount
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                RetryFailedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>まだ終わっていない数。</summary>
    public int PendingCount
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                PauseAllCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>終わった数（完了・失敗・スキップ）。</summary>
    public int FinishedCount { get; private set => SetProperty(ref field, value); }

    /// <summary>項目があるか。</summary>
    public bool HasItems { get; private set => SetProperty(ref field, value); }

    /// <summary>選んでいるタブに行があるか。</summary>
    public bool HasRows
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(HasNoRows));
        }
    }

    /// <summary>選んでいるタブに行がないか（案内を出す）。</summary>
    public bool HasNoRows => !HasRows;

    /// <summary>選んでいるタブに行がないときの案内。</summary>
    public string EmptyText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>見出しの横の件数（例: 完了 377・失敗 2）。</summary>
    public string SummaryText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>全体の進み具合（0〜100、件数で数える）。</summary>
    public double OverallPercent { get; private set => SetProperty(ref field, value); }

    /// <summary>全体の進み具合の文字（例: 76%）。</summary>
    public string OverallPercentText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>終わった数と総数（例: 379 / 500 件）。</summary>
    public string CountText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>全体の速さ（例: 6.3 MB/s）。</summary>
    public string SpeedText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>速さを出しているか。</summary>
    public bool HasSpeed { get; private set => SetProperty(ref field, value); }

    /// <summary>残り時間（例: 残り 約4分）。</summary>
    public string RemainingText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>同時転送の数（例: 同時転送 3）。</summary>
    public string ConcurrencyText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>ステータスバーに出す、転送がないときの文字。</summary>
    public string IdleText => HasItems ? "" : "転送待ちはありません";

    public IRelayCommand RetryFailedCommand { get; }

    public IRelayCommand<TransferTabViewModel> SelectTabCommand { get; }

    public IRelayCommand ToggleExpandedCommand { get; }

    /// <summary>すべて一時停止する。止まっているときは再開する。</summary>
    public IRelayCommand PauseAllCommand { get; }

    /// <summary>終わった項目（完了・スキップ・取り消し）を一覧から消す。</summary>
    public IRelayCommand ClearFinishedCommand { get; }

    /// <summary>送信待ちを切り替える（接続先が変わったとき）。一覧は作り直す。</summary>
    public void SetSource(ITransferSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(source, _source))
            return;
        lock (_flushLock)
        {
            Detach();
            _rows.Clear();
            _removedIds.Clear();
            _pending.Clear();
            _removals.Clear();
            Attach(source);
        }
    }

    /// <summary>1 件を取り消す。</summary>
    public void Cancel(TransferRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _source.Cancel([row.Id]);
    }

    /// <summary>失敗・取り消しの 1 件を送り直す。</summary>
    public void Retry(TransferRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _source.Retry([row.Id]);
    }

    /// <summary>やり直しを待っている 1 件を、すぐやり直す。</summary>
    public void RetryNow(TransferRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _source.RetryNow([row.Id]);
    }

    /// <summary>1 秒ごとの更新（速さ、残り時間、再開までの秒数）を始める。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _tickTimer ??= _time.CreateTimer(_ => Post(Tick), null, TickInterval, TickInterval);
    }

    /// <summary>タブを選ぶ。</summary>
    public void SelectTab(TransferTab tab)
    {
        var selected = Tabs.First(item => item.Tab == tab);
        if (ReferenceEquals(selected, SelectedTab))
            return;
        SelectedTab.IsSelected = false;
        SelectedTab = selected;
        selected.IsSelected = true;
        Rebuild();
    }

    /// <summary>速さと時間の表示を更新する。</summary>
    public void Tick()
    {
        if (_disposed)
            return;
        var now = _time.GetTimestamp();
        foreach (var row in _rows.Values)
            row.UpdateRate(now);
        RefreshRows(Rows);
        UpdateSummary();
    }

    /// <summary>ためてある変化を反映する。</summary>
    internal void Flush()
    {
        lock (_flushLock)
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            if (_disposed)
                return;

            var membershipChanged = false;
            var timestamp = _time.GetTimestamp();
            var changed = new List<TransferRowViewModel>();
            while (_removals.TryDequeue(out var removal))
            {
                if (removal.Generation != _generation)
                    continue;
                foreach (var id in removal.Ids)
                {
                    _removedIds.Add(id);
                    membershipChanged |= _rows.Remove(id);
                }
            }
            while (_pending.TryDequeue(out var entry))
            {
                var snapshot = entry.Snapshot;
                // 前の送信待ちのものと、消したあとに遅れて届いたものは捨てる
                if (entry.Generation != _generation || _removedIds.Contains(snapshot.Id))
                    continue;
                if (_rows.TryGetValue(snapshot.Id, out var row))
                {
                    membershipChanged |= row.Apply(snapshot, timestamp);
                }
                else
                {
                    row = CreateRow(snapshot, timestamp);
                    _rows[snapshot.Id] = row;
                    membershipChanged = true;
                }
                changed.Add(row);
            }

            if (membershipChanged)
            {
                Rebuild();
            }
            else
            {
                foreach (var row in changed)
                    row.UpdateRate(timestamp);
                RefreshRows(changed);
                UpdateSummary();
            }
        }
    }

    /// <summary>見本用に、行の速さを決める。</summary>
    internal void SetRate(Guid id, double bytesPerSecond)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            row.SetRate(bytesPerSecond);
            RefreshRows([row]);
            UpdateSummary();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
        _flushTimer?.Dispose();
        _tickTimer?.Dispose();
        _tickTimer = null;
    }

    // 送信待ちを見はじめる（_flushLock の中か、作るときに呼ぶ）
    private void Attach(ITransferSource source)
    {
        _source = source;
        var generation = ++_generation;
        _onItemChanged = (_, snapshot) => OnItemChanged(generation, snapshot);
        _onItemsRemoved = (_, ids) => OnItemsRemoved(generation, ids);
        _onStateChanged = (_, _) => Post(() =>
        {
            if (generation == _generation && !_disposed)
                UpdatePauseState();
        });
        source.ItemChanged += _onItemChanged;
        source.ItemsRemoved += _onItemsRemoved;
        source.StateChanged += _onStateChanged;

        var timestamp = _time.GetTimestamp();
        foreach (var snapshot in source.GetItems())
            _rows[snapshot.Id] = CreateRow(snapshot, timestamp);
        UpdatePauseState();
        Rebuild();
    }

    private void Detach()
    {
        if (_onItemChanged is not null)
            _source.ItemChanged -= _onItemChanged;
        if (_onItemsRemoved is not null)
            _source.ItemsRemoved -= _onItemsRemoved;
        if (_onStateChanged is not null)
            _source.StateChanged -= _onStateChanged;
        _onItemChanged = null;
        _onItemsRemoved = null;
        _onStateChanged = null;
    }

    private void UpdatePauseState()
    {
        IsPaused = _source.IsPaused;
        var reason = _source.StopReason;
        IsStoppedByFailure = IsPaused && reason is not null;
        var restored = _source.RestoredPendingCount;
        PauseText = !IsPaused ? ""
            : reason is not null ? $"接続できないため止まっています（{DisplayText.Describe(reason)}）"
            : restored > 0 ? $"前回の残りが {restored:N0} 件あります。再開すると続きから送ります"
            : "一時停止中";
        PauseAllCommand.NotifyCanExecuteChanged();
    }

    private void TogglePause()
    {
        if (_source.IsPaused)
            _source.Resume();
        else
            _source.Pause();
        UpdatePauseState();
    }

    private TransferRowViewModel CreateRow(TransferItemSnapshot snapshot, long timestamp)
    {
        var row = new TransferRowViewModel(snapshot, _time);
        row.Apply(snapshot, timestamp);
        return row;
    }

    private void OnItemChanged(int generation, TransferItemSnapshot snapshot)
    {
        _pending.Enqueue((generation, snapshot));
        ScheduleFlush();
    }

    private void OnItemsRemoved(int generation, IReadOnlyList<Guid> ids)
    {
        _removals.Enqueue((generation, ids));
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) != 0)
            return;

        if (_flushTimer is null)
            Flush();
        else
            _flushTimer.Change(FlushInterval, Timeout.InfiniteTimeSpan); // 細かい変化をまとめてから反映する
    }

    private void Post(Action action)
    {
        if (_context is null)
            action();
        else
            _context.Post(_ => action(), null);
    }

    private void RetryFailed()
    {
        _source.RetryFailed();
    }

    private void Rebuild()
    {
        var all = _rows.Values;
        var running = new List<TransferRowViewModel>();
        var retrying = new List<TransferRowViewModel>();
        var queued = new List<TransferRowViewModel>();
        var done = new List<TransferRowViewModel>();
        var failed = new List<TransferRowViewModel>();
        foreach (var row in all)
        {
            switch (row.State)
            {
                case TransferState.Running:
                    running.Add(row);
                    break;
                case TransferState.WaitingToRetry:
                    retrying.Add(row);
                    break;
                case TransferState.Queued:
                    queued.Add(row);
                    break;
                case TransferState.Failed:
                    failed.Add(row);
                    break;
                default:
                    done.Add(row);
                    break;
            }
        }

        // 待機中と進行中は送る順、完了と失敗は新しい順
        static int BySequence(TransferRowViewModel a, TransferRowViewModel b) => a.Snapshot.Sequence.CompareTo(b.Snapshot.Sequence);
        static int Newest(TransferRowViewModel a, TransferRowViewModel b)
        {
            var result = Nullable.Compare(b.Snapshot.FinishedAt, a.Snapshot.FinishedAt);
            return result != 0 ? result : b.Snapshot.Sequence.CompareTo(a.Snapshot.Sequence);
        }
        running.Sort(BySequence);
        retrying.Sort(BySequence);
        queued.Sort(BySequence);
        done.Sort(Newest);
        failed.Sort(Newest);

        var active = running.Concat(retrying).OrderBy(row => row.Snapshot.Sequence).ToList();
        List<TransferRowViewModel> visible = SelectedTab.Tab switch
        {
            TransferTab.Active => active,
            TransferTab.Waiting => queued,
            TransferTab.Done => done,
            TransferTab.Failed => failed,
            // すべて: 進行中、失敗（手当てが要る）、待機中、完了の順
            _ => [.. active, .. failed, .. queued, .. done],
        };

        Tabs[0].Count = _rows.Count;
        Tabs[1].Count = active.Count;
        Tabs[2].Count = queued.Count;
        Tabs[3].Count = done.Count;
        Tabs[4].Count = failed.Count;

        TotalCount = _rows.Count;
        CancelledCount = done.Count(row => row.State == TransferState.Cancelled);
        CompletedCount = done.Count - CancelledCount;
        FailedCount = failed.Count;
        PendingCount = active.Count + queued.Count;
        FinishedCount = done.Count + failed.Count;
        PauseAllCommand.NotifyCanExecuteChanged();
        HasItems = _rows.Count > 0;

        // 待機中の何番目か
        _queuePositions.Clear();
        for (var i = 0; i < queued.Count; i++)
            _queuePositions[queued[i].Id] = i + 1;

        var timestamp = _time.GetTimestamp();
        foreach (var row in visible)
            row.UpdateRate(timestamp);
        RefreshRows(visible);
        Rows.SyncTo(visible);
        HasRows = visible.Count > 0;
        EmptyText = SelectedTab.Tab switch
        {
            TransferTab.Active => "転送中の項目はありません",
            TransferTab.Waiting => "待機中の項目はありません",
            TransferTab.Done => "完了した項目はありません",
            TransferTab.Failed => "失敗した項目はありません",
            _ => "ファイルをここにドラッグするか、アップロード・ダウンロードを押すと、転送が始まります",
        };
        UpdateSummary();
        OnPropertyChanged(nameof(IdleText));
    }

    private void RefreshRows(IEnumerable<TransferRowViewModel> rows)
    {
        var now = _time.GetUtcNow();
        var maxAttempts = _source.MaxAttempts;
        var timeZone = _time.LocalTimeZone;
        foreach (var row in rows)
            row.Refresh(now, maxAttempts, _queuePositions.GetValueOrDefault(row.Id), timeZone);
    }

    private void UpdateSummary()
    {
        SummaryText = $"完了 {CompletedCount:N0}・失敗 {FailedCount:N0}";

        var total = TotalCount;
        var finished = FinishedCount;
        var percent = total == 0 ? 0 : finished == total ? 100 : Math.Min(99, (int)Math.Round(finished * 100d / total, MidpointRounding.AwayFromZero));
        OverallPercent = percent;
        OverallPercentText = $"{percent}%";
        CountText = $"{finished:N0} / {total:N0} 件";
        ConcurrencyText = $"同時転送 {_source.MaxConcurrency}";

        double speed = 0;
        long remainingBytes = 0;
        foreach (var row in _rows.Values)
        {
            if (row.State == TransferState.Running)
                speed += row.BytesPerSecond;
            remainingBytes += row.RemainingBytes;
        }
        HasSpeed = speed > 0;
        SpeedText = DisplayText.Speed(speed);
        RemainingText = speed > 0 && remainingBytes > 0
            ? DisplayText.Remaining(TimeSpan.FromSeconds(remainingBytes / speed))
            : "";
    }
}
