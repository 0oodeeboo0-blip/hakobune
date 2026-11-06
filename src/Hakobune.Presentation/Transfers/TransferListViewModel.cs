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

    private readonly ITransferSource _source;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private readonly Dictionary<Guid, TransferRowViewModel> _rows = [];
    private readonly ConcurrentQueue<TransferItemSnapshot> _pending = new();
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
        _source = source;
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
        PauseAllCommand = new RelayCommand(() => PauseRequested?.Invoke(this, EventArgs.Empty), () => PendingCount > 0);

        var timestamp = _time.GetTimestamp();
        foreach (var snapshot in source.GetItems())
            _rows[snapshot.Id] = CreateRow(snapshot, timestamp);
        source.ItemChanged += OnItemChanged;
        Rebuild();
    }

    /// <summary>「すべて一時停止」が押された。</summary>
    public event EventHandler? PauseRequested;

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
    public int CompletedCount { get; private set => SetProperty(ref field, value); }

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

    public IRelayCommand PauseAllCommand { get; }

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
            while (_pending.TryDequeue(out var snapshot))
            {
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
        _source.ItemChanged -= OnItemChanged;
        _flushTimer?.Dispose();
        _tickTimer?.Dispose();
        _tickTimer = null;
    }

    private TransferRowViewModel CreateRow(TransferItemSnapshot snapshot, long timestamp)
    {
        var row = new TransferRowViewModel(snapshot, _time);
        row.Apply(snapshot, timestamp);
        return row;
    }

    private void OnItemChanged(object? sender, TransferItemSnapshot snapshot)
    {
        _pending.Enqueue(snapshot);
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
        CompletedCount = done.Count;
        FailedCount = failed.Count;
        PendingCount = active.Count + queued.Count;
        FinishedCount = done.Count + failed.Count;
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
