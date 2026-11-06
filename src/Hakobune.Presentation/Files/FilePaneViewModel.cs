using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Logging;
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

/// <summary>一覧の上でキーを押して頼む操作。</summary>
public enum PaneAction
{
    /// <summary>選んだものを、もう一方へ送る（パソコン側ならアップロード、サーバー側ならダウンロード）。</summary>
    Transfer,

    /// <summary>新しいフォルダ。</summary>
    NewFolder,

    /// <summary>名前の変更。</summary>
    Rename,

    /// <summary>削除。</summary>
    Delete,

    /// <summary>属性の変更（サーバー側だけ）。</summary>
    ChangePermissions,
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

    /// <summary>
    /// 一覧を作る。<paramref name="timeProvider"/> は、時刻の表示、自動の取り直し、増えた・変わった行を光らせる時間に使う。
    /// </summary>
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

    /// <summary>取り直しで増えた・変わった行を光らせておく時間（画面の光も、この長さで消える）。</summary>
    public static TimeSpan HighlightDuration { get; } = TimeSpan.FromMilliseconds(3200);

    /// <summary>左右どちらか。</summary>
    public PaneKind Kind { get; }

    /// <summary>見出し。</summary>
    public string Title => Kind == PaneKind.Local ? "ローカル" : "サーバー";

    /// <summary>属性の列を出すか（サーバー側だけ）。</summary>
    public bool ShowsPermissions => Kind == PaneKind.Remote;

    /// <summary>行の高さを詰めるか（設定の「コンパクト」）。</summary>
    public bool UseCompactRows { get; set => SetProperty(ref field, value); }

    /// <summary>一覧を取れなかったことを書くログ（null なら書かない）。</summary>
    public ActivityLog? Log { get; set; }

    /// <summary>ログに書く、どこのことか（サーバー側なら接続先の名前）。</summary>
    public string? LogSource { get; set; }

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
        SelectedRows = [];
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

    /// <summary>今のフォルダを取り直し、その名前の行を選ぶ（作った・名前を変えたものを見せる）。</summary>
    public Task RefreshAsync(string focusName)
    {
        ArgumentNullException.ThrowIfNull(focusName);
        return HasSource && _hasLoaded ? LoadAsync(CurrentPath, focusName) : RetryAsync();
    }

    /// <summary>
    /// ファイルが落とされた（エクスプローラーから、パスで）。落とされた側が受け取り、どうするかは <see cref="FilesDropped"/> を見る側が決める。
    /// </summary>
    public void DropFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > 0)
            FilesDropped?.Invoke(this, paths);
    }

    /// <summary>もう一方の一覧の行が落とされた。</summary>
    public void DropRows(FilePaneViewModel from, IReadOnlyList<FileRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(rows);
        if (!ReferenceEquals(from, this) && rows.Count > 0)
            RowsDropped?.Invoke(this, new RowsDroppedEventArgs(from, rows));
    }

    /// <summary>エクスプローラーからファイルが落とされた。</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    /// <summary>もう一方の一覧の行が落とされた。</summary>
    public event EventHandler<RowsDroppedEventArgs>? RowsDropped;

    /// <summary>行を開く（フォルダなら中へ移る）。</summary>
    public Task OpenAsync(FileRowViewModel? row) =>
        row is { IsDirectory: true } ? LoadAsync(row.Entry.FullPath, focusName: null) : Task.CompletedTask;

    /// <summary>
    /// 一覧の外で起きたこと（接続できなかったなど）を知らせる。<paramref name="retry"/> を渡すと「今すぐ再試行」で呼ぶ。
    /// </summary>
    public void ShowNotice(string title, string reason, string text = "", Func<Task>? retry = null, string? retryText = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(reason);
        ShowMessage(title, reason, text ?? "");
        _externalRetry = retry;
        RetryText = retryText ?? DefaultRetryText;
        RetryCommand.NotifyCanExecuteChanged();
    }

    private const string DefaultRetryText = "今すぐ再試行";

    /// <summary>知らせのボタンの文字（ふつうは「今すぐ再試行」。直し方があれば「FTPS（明示的）でつなぎ直す」など）。</summary>
    public string RetryText { get; private set => SetProperty(ref field, value); } = DefaultRetryText;

    /// <summary>「接続しています…」のように、一覧の代わりに出す文字を決める。null で戻す。</summary>
    public void SetBusy(string? text)
    {
        _busyText = text;
        OnViewStateChanged();
    }

    /// <summary>画面でこちら側に触れたことを知らせる。</summary>
    public void Activate() => ActivationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>キーで操作を頼む（実際の操作はメイン画面が行う）。</summary>
    public void RequestAction(PaneAction action) => ActionRequested?.Invoke(this, action);

    /// <summary>キーで操作を頼まれた。</summary>
    public event EventHandler<PaneAction>? ActionRequested;

    /// <summary>画面で選んでいる数を知らせる。</summary>
    public void UpdateSelection(int selectedCount) => SelectedCount = Math.Max(0, selectedCount);

    /// <summary>画面で選んでいる行を知らせる（アップロード・ダウンロードの対象になる）。</summary>
    public void UpdateSelection(IReadOnlyList<FileRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        SelectedRows = rows;
        SelectedCount = rows.Count;
    }

    /// <summary>選んでいる行。</summary>
    public IReadOnlyList<FileRowViewModel> SelectedRows { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>一覧を出している相手。</summary>
    public IFileSource? Source => _source;

    /// <summary>いまのフォルダの一覧を取れているか（送り先にできるか）。</summary>
    public bool HasLoaded => _hasLoaded;

    /// <summary>いまのフォルダに、その名前の項目があるか（絞り込みに関係なく。パソコン側は大文字と小文字を区別しない）。</summary>
    public bool HasItemNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var comparison = Kind == PaneKind.Local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return _rows.Any(row => string.Equals(row.Name, name, comparison));
    }

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
            _rows = MakeRows(listing.Entries, samePath);
            // 同じフォルダの取り直しなら、残った行の選択はそのまま
            SelectedRows = samePath ? [.. SelectedRows.Where(_rows.Contains)] : [];
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
            Log?.Warning($"一覧を更新できませんでした（{requestedPath}）: {DisplayText.Describe(failure)}。{state}", LogSource);
            return;
        }

        // 別のフォルダを開けなかった。今のフォルダにとどまる。
        _failedPath = requestedPath;
        var title = _hasLoaded ? "フォルダを開けませんでした" : "一覧を取得できませんでした";
        ShowMessage(title, DisplayText.Describe(failure), "");
        Log?.Error($"{title}（{requestedPath}）: {DisplayText.Describe(failure)}", LogSource);
        OnViewStateChanged();
    }

    // 行を作る。同じフォルダの取り直しでは、変わっていない項目の行はそのまま使う
    // （行を入れ替えないので、一覧が動かず、選択とスクロールの位置も残る）。
    // 増えた・変わった項目の行は、しばらく光らせる（アップロードしたものがどれか分かるように）
    private List<FileRowViewModel> MakeRows(IReadOnlyList<FileEntry> entries, bool samePath)
    {
        Dictionary<FileEntry, FileRowViewModel>? current = null;
        if (samePath)
        {
            current = new Dictionary<FileEntry, FileRowViewModel>(_rows.Count);
            foreach (var row in _rows)
                current.TryAdd(row.Entry, row);
        }
        var rows = new List<FileRowViewModel>(entries.Count);
        var fresh = new List<FileRowViewModel>();
        foreach (var entry in entries)
        {
            if (current is not null && current.Remove(entry, out var kept))
            {
                rows.Add(kept);
                continue;
            }
            var row = new FileRowViewModel(entry, _time.LocalTimeZone, ShowsPermissions);
            rows.Add(row);
            if (samePath)
                fresh.Add(row);
        }
        if (fresh.Count > 0)
            Highlight(fresh);
        return rows;
    }

    // 光らせ、時間がたったら戻す（そのあとで行が画面に出てきても光らない）
    private void Highlight(List<FileRowViewModel> rows)
    {
        foreach (var row in rows)
            row.IsHighlighted = true;
        _ = EndHighlightAsync(rows);
    }

    private async Task EndHighlightAsync(List<FileRowViewModel> rows)
    {
        await Task.Delay(HighlightDuration, _time).ConfigureAwait(true);
        foreach (var row in rows)
            row.IsHighlighted = false;
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
        RetryText = DefaultRetryText;
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

/// <summary>一覧の行が、もう一方の一覧へ落とされた。</summary>
/// <param name="From">行を持ってきた一覧。</param>
/// <param name="Rows">落とされた行。</param>
public sealed record RowsDroppedEventArgs(FilePaneViewModel From, IReadOnlyList<FileRowViewModel> Rows);
