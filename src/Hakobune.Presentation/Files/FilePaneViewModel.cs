using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Text;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Files;

/// <summary>左右どちらの一覧か。</summary>
public enum PaneKind
{
    /// <summary>パソコン側。</summary>
    Local,

    /// <summary>サーバー側。</summary>
    Remote,
}

/// <summary>
/// 一覧 1 つ分（パソコン側またはサーバー側）。
/// </summary>
/// <remarks>
/// 一覧の取り直しに失敗しても、表示中の一覧は消さない。前回の一覧を残したまま知らせ、一時的な失敗なら少し待って自動で取り直す。
/// 別のフォルダを開けなかったときは、今のフォルダにとどまる。
/// </remarks>
public sealed class FilePaneViewModel : ObservableObject
{
    private static readonly TimeSpan[] AutoRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private static readonly CompareInfo FilterCompare = CultureInfo.GetCultureInfo("ja-JP").CompareInfo;

    private const CompareOptions FilterOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    private readonly TimeProvider _time;
    private List<FileRowViewModel> _rows = [];
    private IFileSource? _source;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _autoRetryCancellation;
    private int _loadVersion;
    private int _autoRetryCount;
    private string? _failedPath;
    private bool _hasLoaded;
    private Func<Task>? _externalRetry;
    private string? _busyText;

    /// <summary>一覧を作る。<paramref name="timeProvider"/> は時刻の表示と自動の取り直しに使う。</summary>
    public FilePaneViewModel(PaneKind kind, TimeProvider? timeProvider = null)
    {
        Kind = kind;
        _time = timeProvider ?? TimeProvider.System;
        GoUpCommand = new AsyncRelayCommand(GoUpAsync, () => CanGoUp);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => HasSource);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => HasSource || _externalRetry is not null);
        OpenCommand = new AsyncRelayCommand<FileRowViewModel>(OpenAsync);
        NavigateToSegmentCommand = new AsyncRelayCommand<PathSegment>(segment => segment is null ? Task.CompletedTask : NavigateAsync(segment.Path));
    }

    /// <summary>左右どちらか。</summary>
    public PaneKind Kind { get; }

    /// <summary>見出し。</summary>
    public string Title => Kind == PaneKind.Local ? "ローカル" : "サーバー";

    /// <summary>属性の列を出すか（サーバー側だけ）。</summary>
    public bool ShowsPermissions => Kind == PaneKind.Remote;

    /// <summary>パソコン側か。</summary>
    public bool IsLocal => Kind == PaneKind.Local;

    /// <summary>サーバー側か。</summary>
    public bool IsRemote => Kind == PaneKind.Remote;

    /// <summary>一覧を出す相手があるか（サーバー側は接続しているか）。</summary>
    public bool HasSource => _source is not null;

    /// <summary>操作の対象になっている側か（最後に触った側）。ツールバーの操作はこちらに効く。</summary>
    public bool IsActive { get; internal set => SetProperty(ref field, value); }

    /// <summary>画面でこちら側に触れた（フォーカスが来た）。</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>今のフォルダ。</summary>
    public string CurrentPath { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>パンくず。</summary>
    public ObservableList<PathSegment> Breadcrumbs { get; } = [];

    /// <summary>表示している行（絞り込みと並べ替えのあと）。</summary>
    public ObservableList<FileRowViewModel> Items { get; } = [];

    /// <summary>今のフォルダの項目の数（絞り込みの前）。</summary>
    public int TotalCount => _rows.Count;

    /// <summary>絞り込みの文字。</summary>
    public string FilterText
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
                ApplyView(keepFocus: false);
        }
    } = "";

    /// <summary>並べ替えの基準。</summary>
    public FileSortColumn SortColumn
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(IsSortedByName));
                OnPropertyChanged(nameof(IsSortedBySize));
                OnPropertyChanged(nameof(IsSortedByModified));
            }
        }
    }

    /// <summary>逆順か。</summary>
    public bool SortDescending
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(SortArrowAngle));
        }
    }

    // 列の見出しに並べ替えの矢印を出す
    public bool IsSortedByName => SortColumn == FileSortColumn.Name;

    public bool IsSortedBySize => SortColumn == FileSortColumn.Size;

    public bool IsSortedByModified => SortColumn == FileSortColumn.Modified;

    /// <summary>並べ替えの矢印の向き（上向きの矢印を何度回すか）。</summary>
    public double SortArrowAngle => SortDescending ? 180 : 0;

    /// <summary>選んでいる項目の数。画面から知らせてもらう。</summary>
    public int SelectedCount
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(SummaryText));
        }
    }

    /// <summary>一覧を取っている途中か。</summary>
    public bool IsLoading
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnViewStateChanged();
        }
    }

    /// <summary>取り直しに失敗し、前回の一覧を出しているか。</summary>
    public bool IsStale
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(SummaryText));
        }
    }

    /// <summary>今の一覧を取った時刻。</summary>
    public DateTimeOffset? RetrievedAt { get; private set => SetProperty(ref field, value); }

    /// <summary>お知らせ（InfoBar）を出しているか。</summary>
    public bool HasMessage { get; private set => SetProperty(ref field, value); }

    /// <summary>お知らせの見出し（何が起きたか）。</summary>
    public string MessageTitle { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>お知らせの理由（例: 接続が切れました）。見出しの横に出す。</summary>
    public string MessageReason { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>お知らせの本文（いまの様子。例: 前回の一覧を表示しています）。</summary>
    public string MessageText
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(HasMessageText));
        }
    } = "";

    /// <summary>お知らせの本文があるか。</summary>
    public bool HasMessageText => MessageText.Length > 0;

    /// <summary>
    /// 一覧を出したあとに選んで見える位置に出す行（上のフォルダに戻ったときの元のフォルダなど）。画面が読んで使う。
    /// </summary>
    public FileRowViewModel? FocusItem { get; private set => SetProperty(ref field, value); }

    /// <summary>見出しの横に出す件数など（例: 12 項目・1 個を選択）。</summary>
    public string SummaryText
    {
        get
        {
            if (!HasSource)
                return "";
            if (!_hasLoaded)
                return IsLoading ? "読み込み中…" : "";
            if (FilterText.Trim().Length > 0)
                return $"{Items.Count} 件が一致";

            var text = $"{_rows.Count} 項目";
            if (SelectedCount > 0)
                text += $"・{SelectedCount} 個を選択";
            if (IsStale && RetrievedAt is { } retrievedAt)
                text += $"・{FormatTime(retrievedAt)} 時点";
            return text;
        }
    }

    /// <summary>行がないときの案内を出すか（最初の読み込み中は出さない）。</summary>
    public bool ShowsEmptyText => Items.Count == 0 && (_busyText is not null || !(IsLoading && !_hasLoaded));

    /// <summary>最初の読み込み中か、接続しているところか（くるくる回る印を出す）。</summary>
    public bool ShowsProgress => _busyText is not null || (IsLoading && !_hasLoaded);

    /// <summary>行がないときの案内。</summary>
    public string EmptyText
    {
        get
        {
            if (_busyText is not null)
                return _busyText;
            if (!HasSource)
                return Kind == PaneKind.Remote ? "接続していません" : "フォルダを開いていません";
            if (!_hasLoaded)
                return IsLoading ? "" : "フォルダを開けませんでした";
            if (_rows.Count == 0)
                return "このフォルダは空です";
            return "一致する項目はありません";
        }
    }

    /// <summary>行がないときの補足。</summary>
    public string EmptyHint => !HasSource && _busyText is null && Kind == PaneKind.Remote ? DisconnectedHint : "";

    /// <summary>接続していないときの補足（接続先がまだ無いときは、作り方を案内する）。</summary>
    public string DisconnectedHint
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
                OnPropertyChanged(nameof(EmptyHint));
        }
    } = "左の接続先を選ぶと、ここにサーバーのファイルが出ます";

    /// <summary>上のフォルダへ行けるか。</summary>
    public bool CanGoUp => _hasLoaded && _source?.GetParent(CurrentPath) is not null;

    public IAsyncRelayCommand GoUpCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    public IAsyncRelayCommand<FileRowViewModel> OpenCommand { get; }

    public IAsyncRelayCommand<PathSegment> NavigateToSegmentCommand { get; }

    /// <summary>
    /// 一覧を出す相手を決め、最初のフォルダを開く。null なら空にする（サーバーとの接続を切ったとき）。
    /// </summary>
    public async Task SetSourceAsync(IFileSource? source, string? startPath = null, CancellationToken cancellationToken = default)
    {
        CancelLoading();
        CancelAutoRetry();
        _source = source;
        _rows = [];
        _hasLoaded = false;
        _failedPath = null;
        _autoRetryCount = 0;
        CurrentPath = "";
        Breadcrumbs.Clear();
        IsStale = false;
        RetrievedAt = null;
        HideMessage();
        FilterText = "";
        ApplyView(keepFocus: false);
        OnPropertyChanged(nameof(HasSource));
        RefreshCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();

        if (source is null)
            return;

        string path;
        IsLoading = true;
        try
        {
            path = startPath ?? await source.GetStartPathAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(source, _source))
                IsLoading = false;
            return;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(source, _source))
            {
                IsLoading = false;
                ShowMessage("一覧を取得できませんでした", DisplayText.Describe(FailureClassifier.FromException(ex)), "");
            }
            return;
        }
        if (!ReferenceEquals(source, _source))
            return;
        await LoadAsync(path, focusName: null).ConfigureAwait(true);
    }

    /// <summary>フォルダを開く。開けなければ今のフォルダにとどまる。</summary>
    public Task NavigateAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return LoadAsync(path, focusName: null);
    }

    /// <summary>今のフォルダを取り直す。失敗しても今の一覧は消さない。</summary>
    public Task RefreshAsync() => HasSource && _hasLoaded ? LoadAsync(CurrentPath, focusName: null) : RetryAsync();

    /// <summary>行を開く（フォルダなら中へ移る）。</summary>
    public Task OpenAsync(FileRowViewModel? row) =>
        row is { IsDirectory: true } ? LoadAsync(row.Entry.FullPath, focusName: null) : Task.CompletedTask;

    /// <summary>
    /// 一覧の外で起きたこと（接続できなかったなど）を知らせる。<paramref name="retry"/> を渡すと「今すぐ再試行」で呼ぶ。
    /// </summary>
    public void ShowNotice(string title, string reason, string text = "", Func<Task>? retry = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(reason);
        ShowMessage(title, reason, text ?? "");
        _externalRetry = retry;
        RetryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>「接続しています…」のように、一覧の代わりに出す文字を決める。null で戻す。</summary>
    public void SetBusy(string? text)
    {
        _busyText = text;
        OnViewStateChanged();
    }

    /// <summary>画面でこちら側に触れたことを知らせる。</summary>
    public void Activate() => ActivationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>画面で選んでいる数を知らせる。</summary>
    public void UpdateSelection(int selectedCount) => SelectedCount = Math.Max(0, selectedCount);

    /// <summary>名前で行を選んで見える位置に出す（画面が <see cref="FocusItem"/> を読んで選ぶ）。</summary>
    public bool Focus(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var row = Items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        FocusItem = row;
        return row is not null;
    }

    /// <summary>並べ替える。同じ基準をもう一度選ぶと逆順にする。</summary>
    public void SortBy(FileSortColumn column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }
        ApplyView(keepFocus: true);
    }

    private Task GoUpAsync()
    {
        var parent = _source?.GetParent(CurrentPath);
        if (parent is null)
            return Task.CompletedTask;
        // 戻ったあと、いままでいたフォルダを選んでおく（エクスプローラーと同じ）
        var current = Breadcrumbs.Count > 0 ? Breadcrumbs[^1].Label : null;
        return LoadAsync(parent, focusName: current);
    }

    private Task RetryAsync()
    {
        if (_externalRetry is { } retry)
        {
            HideMessage();
            return retry();
        }
        if (!HasSource)
            return Task.CompletedTask;
        if (_failedPath is { } failed)
            return LoadAsync(failed, focusName: null);
        if (_hasLoaded)
            return LoadAsync(CurrentPath, focusName: null);
        return SetSourceAsync(_source);
    }

    private async Task LoadAsync(string path, string? focusName)
    {
        var source = _source;
        if (source is null)
            return;

        CancelAutoRetry();
        CancelLoading();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var version = ++_loadVersion;
        IsLoading = true;

        FileListing listing;
        try
        {
            listing = await source.ListAsync(path, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // 一覧を出す相手は失敗を例外にしない決まりだが、念のため
            listing = new FileListing(path, [], FailureClassifier.FromException(ex), _time.GetUtcNow());
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }

        if (version != _loadVersion || !ReferenceEquals(source, _source))
            return;
        Apply(path, listing, focusName);
    }

    private void Apply(string requestedPath, FileListing listing, string? focusName)
    {
        if (listing.Failure is not { } failure)
        {
            _failedPath = null;
            _autoRetryCount = 0;
            var samePath = _hasLoaded && PathEquals(listing.Path, CurrentPath);
            _hasLoaded = true;
            CurrentPath = listing.Path;
            UpdateBreadcrumbs();
            _rows = listing.Entries.Select(entry => new FileRowViewModel(entry, _time.LocalTimeZone, ShowsPermissions)).ToList();
            RetrievedAt = listing.RetrievedAt;
            IsStale = false;
            HideMessage();
            if (!samePath)
                FilterText = "";
            ApplyView(keepFocus: false);
            FocusItem = focusName is null ? null : Items.FirstOrDefault(row => string.Equals(row.Name, focusName, StringComparison.Ordinal));
            GoUpCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanGoUp));
            return;
        }

        if (_hasLoaded && PathEquals(requestedPath, CurrentPath))
        {
            // 取り直しに失敗した。今の一覧は残す。
            _failedPath = null;
            IsStale = true;
            var state = "前回の一覧を表示しています";
            if (failure.IsTransient)
            {
                var delay = AutoRetryDelays[Math.Min(_autoRetryCount, AutoRetryDelays.Length - 1)];
                state += $"（{(int)delay.TotalSeconds}秒後に再試行）";
                ScheduleAutoRetry(delay);
            }
            ShowMessage("一覧を更新できませんでした", DisplayText.Describe(failure), state);
            return;
        }

        // 別のフォルダを開けなかった。今のフォルダにとどまる。
        _failedPath = requestedPath;
        ShowMessage(_hasLoaded ? "フォルダを開けませんでした" : "一覧を取得できませんでした", DisplayText.Describe(failure), "");
        OnViewStateChanged();
    }

    private void ApplyView(bool keepFocus)
    {
        var filter = FilterText.Trim();
        IEnumerable<FileRowViewModel> rows = _rows;
        if (filter.Length > 0)
            rows = rows.Where(row => FilterCompare.IndexOf(row.Name, filter, FilterOptions) >= 0);
        var sorted = rows.Order(new FileRowComparer(SortColumn, SortDescending)).ToList();

        var focus = keepFocus ? FocusItem : null;
        Items.SyncTo(sorted);
        FocusItem = focus is not null && sorted.Contains(focus) ? focus : null;
        OnPropertyChanged(nameof(TotalCount));
        OnViewStateChanged();
    }

    private void OnViewStateChanged()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ShowsEmptyText));
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyHint));
    }

    private void UpdateBreadcrumbs()
    {
        var segments = _source?.GetSegments(CurrentPath) ?? [];
        Breadcrumbs.SyncTo(segments);
    }

    private void ShowMessage(string title, string reason, string text)
    {
        MessageTitle = title;
        MessageReason = reason;
        MessageText = text;
        HasMessage = true;
    }

    private void HideMessage()
    {
        if (_externalRetry is not null)
        {
            _externalRetry = null;
            RetryCommand.NotifyCanExecuteChanged();
        }
        HasMessage = false;
        MessageTitle = "";
        MessageReason = "";
        MessageText = "";
    }

    private void ScheduleAutoRetry(TimeSpan delay)
    {
        CancelAutoRetry();
        _autoRetryCount++;
        var cancellation = new CancellationTokenSource();
        _autoRetryCancellation = cancellation;
        _ = AutoRetryAsync(delay, cancellation.Token);
    }

    private async Task AutoRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!cancellationToken.IsCancellationRequested)
            await LoadAsync(CurrentPath, focusName: null).ConfigureAwait(true);
    }

    private void CancelAutoRetry()
    {
        _autoRetryCancellation?.Cancel();
        _autoRetryCancellation?.Dispose();
        _autoRetryCancellation = null;
    }

    private void CancelLoading()
    {
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        _loadVersion++;
        IsLoading = false;
    }

    private string FormatTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.Ordinal);
}
