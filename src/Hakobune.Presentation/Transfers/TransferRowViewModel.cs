using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Hakobune.Core.Text;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>転送の一覧の 1 行。</summary>
public sealed class TransferRowViewModel : ObservableObject
{
    private readonly RateMeter _rate;
    private double? _fixedRate;

    internal TransferRowViewModel(TransferItemSnapshot snapshot, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Id = snapshot.Id;
        Direction = snapshot.Direction;
        Name = TransferPaths.GetFileName(snapshot.RemotePath);
        Destination = snapshot.IsDelete
            ? "サーバーから消す（" + TransferPaths.GetRemoteParent(snapshot.RemotePath) + "）"
            : "→ " + (snapshot.Direction == TransferDirection.Upload
                ? TransferPaths.GetRemoteParent(snapshot.RemotePath)
                : TransferPaths.GetLocalParent(snapshot.LocalPath));
        Snapshot = snapshot;
        _rate = new RateMeter(timeProvider);
    }

    /// <summary>項目の ID。</summary>
    public Guid Id { get; }

    /// <summary>向き。</summary>
    public TransferDirection Direction { get; }

    /// <summary>アップロードか（サーバーから消す依頼は除く）。</summary>
    public bool IsUpload => Direction == TransferDirection.Upload && !IsDelete;

    /// <summary>ダウンロードか。</summary>
    public bool IsDownload => Direction == TransferDirection.Download;

    /// <summary>サーバーのファイルかフォルダを消す依頼か（同期で「サーバーからも消す」を選んだとき）。</summary>
    public bool IsDelete => Snapshot.IsDelete;

    /// <summary>向きの名前（読み上げ用）。</summary>
    public string DirectionLabel => IsDelete ? "削除" : IsUpload ? "アップロード" : "ダウンロード";

    /// <summary>ファイル名（空のフォルダを作る依頼なら、フォルダの名前）。</summary>
    public string Name { get; }

    /// <summary>空のフォルダを作る依頼か。</summary>
    public bool IsFolder => Snapshot.IsFolder;

    /// <summary>送り先のフォルダ（例: → /var/www/html）。</summary>
    public string Destination { get; }

    /// <summary>いまの様子。</summary>
    public TransferItemSnapshot Snapshot { get; private set; }

    /// <summary>状態。</summary>
    public TransferState State => Snapshot.State;

    /// <summary>状態の名前（例: 再試行待ち）。</summary>
    public string StateLabel { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>補足（例: 3.4 MB/s・残り 2秒）。</summary>
    public string Detail { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>進み具合（0〜100）。</summary>
    public double Percent { get; private set => SetProperty(ref field, value); }

    /// <summary>進み具合の文字（例: 62%）。</summary>
    public string PercentText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>いまの速さ（バイト/秒）。</summary>
    public double BytesPerSecond { get; private set; }

    /// <summary>まだ送っていない量（バイト）。</summary>
    public long RemainingBytes => State is TransferState.Completed or TransferState.Failed or TransferState.Skipped or TransferState.Cancelled
        ? 0
        : Math.Max(0, Snapshot.TotalBytes - Snapshot.TransferredBytes);

    // 状態ごとの出し分け（画面の x:Bind で Visibility に使う）
    public bool IsRunning { get; private set => SetProperty(ref field, value); }

    public bool ShowsStatus { get; private set => SetProperty(ref field, value); } = true;

    public bool IsRetrying { get; private set => SetProperty(ref field, value); }

    public bool IsFailed { get; private set => SetProperty(ref field, value); }

    public bool IsDone { get; private set => SetProperty(ref field, value); }

    public bool IsQueued { get; private set => SetProperty(ref field, value); }

    /// <summary>状態の名前をふつうの色で出すか（完了・待機中）。</summary>
    public bool ShowsPlainLabel { get; private set => SetProperty(ref field, value); }

    public bool CanPause { get; private set => SetProperty(ref field, value); }

    public bool CanCancel { get; private set => SetProperty(ref field, value); }

    public bool CanRetryNow { get; private set => SetProperty(ref field, value); }

    public bool CanRetry { get; private set => SetProperty(ref field, value); }

    public bool CanShowDetails { get; private set => SetProperty(ref field, value); }

    /// <summary>失敗の詳しい説明（「詳細」で出す。サーバーの返事、パスなど）。</summary>
    public string FailureDetails { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>読み上げ用の説明。</summary>
    public string AccessibleName => $"{DirectionLabel} {Name}、{StateLabel}、{Detail}";

    /// <summary>一覧の項目を読み上げるときの名前になる。</summary>
    public override string ToString() => AccessibleName;

    /// <summary>新しい様子を取り込む。状態（どのタブに出るか）が変わったら true。</summary>
    internal bool Apply(TransferItemSnapshot snapshot, long timestamp)
    {
        var stateChanged = snapshot.State != Snapshot.State;
        Snapshot = snapshot;
        if (snapshot.State == TransferState.Running)
            _rate.Add(timestamp, snapshot.TransferredBytes);
        else
            _rate.Reset();
        return stateChanged;
    }

    /// <summary>速さを測り直す。</summary>
    internal void UpdateRate(long now) =>
        BytesPerSecond = State != TransferState.Running ? 0 : _fixedRate ?? _rate.GetBytesPerSecond(now);

    /// <summary>速さを決めて出す（見本用）。以後は測り直さない。</summary>
    internal void SetRate(double bytesPerSecond)
    {
        _fixedRate = bytesPerSecond;
        BytesPerSecond = bytesPerSecond;
    }

    /// <summary>表示を作り直す。</summary>
    internal void Refresh(DateTimeOffset now, int maxAttempts, int queuePosition, TimeZoneInfo timeZone)
    {
        var snapshot = Snapshot;
        var state = snapshot.State;

        IsRunning = state == TransferState.Running;
        ShowsStatus = !IsRunning;
        IsRetrying = state == TransferState.WaitingToRetry;
        IsFailed = state == TransferState.Failed;
        IsDone = state is TransferState.Completed or TransferState.Skipped or TransferState.Cancelled;
        IsQueued = state == TransferState.Queued;
        ShowsPlainLabel = IsDone || IsQueued;
        // 1 件だけ止めたいときは「取り消し」にして、あとで「再送」する（続きから送る）
        CanPause = false;
        CanCancel = IsRunning || IsRetrying || IsQueued;
        CanRetryNow = IsRetrying;
        CanRetry = IsFailed || state == TransferState.Cancelled;
        CanShowDetails = IsFailed;
        FailureDetails = IsFailed ? DescribeFailure(snapshot) : "";

        var percent = snapshot.TotalBytes > 0
            ? Math.Clamp(snapshot.TransferredBytes * 100d / snapshot.TotalBytes, 0, 100)
            : state == TransferState.Completed ? 100 : 0;
        Percent = percent;
        PercentText = $"{(int)percent}%";

        StateLabel = DisplayText.StateLabel(state);
        Detail = state switch
        {
            TransferState.Running when snapshot.IsDelete => "消しています",
            TransferState.Running => RunningDetail(snapshot),
            TransferState.WaitingToRetry => RetryDetail(snapshot, now, maxAttempts),
            TransferState.Queued => queuePosition > 0 ? $"{queuePosition.ToString(CultureInfo.InvariantCulture)}番目" : "",
            TransferState.Completed => CompletedDetail(snapshot, timeZone),
            TransferState.Failed => FailedDetail(snapshot),
            TransferState.Skipped => snapshot.Note ?? "",
            TransferState.Cancelled => snapshot.FinishedAt is { } at
                ? TimeZoneInfo.ConvertTime(at, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture)
                : "",
            _ => "",
        };
        OnPropertyChanged(nameof(AccessibleName));
    }

    // 「詳細」の中身: 理由、サーバーの返事、回数、パス
    private string DescribeFailure(TransferItemSnapshot snapshot)
    {
        var lines = new List<string>();
        if (snapshot.LastFailure is { } failure)
        {
            lines.Add(DisplayText.Describe(failure));
            if (!string.IsNullOrWhiteSpace(failure.ServerMessage))
                lines.Add("サーバーの返事: " + failure.ServerMessage.Trim());
        }
        if (snapshot.FailedAttempts > 1)
            lines.Add($"{snapshot.FailedAttempts.ToString(CultureInfo.InvariantCulture)}回試しました");
        if (IsDelete)
        {
            lines.Add("消すもの: " + snapshot.RemotePath);
        }
        else
        {
            lines.Add((IsUpload ? "送るファイル: " : "受け取る先: ") + snapshot.LocalPath);
            lines.Add((IsUpload ? "送り先: " : "サーバーのファイル: ") + snapshot.RemotePath);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private string RunningDetail(TransferItemSnapshot snapshot)
    {
        if (BytesPerSecond > 0)
        {
            var speed = DisplayText.Speed(BytesPerSecond);
            if (snapshot.TotalBytes <= 0)
                return speed;
            var remaining = DisplayText.Remaining(TimeSpan.FromSeconds(RemainingBytes / BytesPerSecond));
            return remaining.Length > 0 ? $"{speed}・{remaining}" : speed;
        }
        return snapshot.TotalBytes > 0
            ? $"{DisplayText.Size(snapshot.TransferredBytes)} / {DisplayText.Size(snapshot.TotalBytes)}"
            : "開始しています";
    }

    private static string RetryDetail(TransferItemSnapshot snapshot, DateTimeOffset now, int maxAttempts)
    {
        var wait = snapshot.RetryAt is { } at && at > now
            ? $"{((int)Math.Ceiling((at - now).TotalSeconds)).ToString(CultureInfo.InvariantCulture)}秒後に再開"
            : "まもなく再開";
        return $"{DisplayText.RetryReason(snapshot.LastFailure)}・{snapshot.FailedAttempts + 1}/{maxAttempts}回目・{wait}";
    }

    private static string CompletedDetail(TransferItemSnapshot snapshot, TimeZoneInfo timeZone)
    {
        var parts = new List<string>(3);
        if (snapshot.IsDelete)
            parts.Add("サーバーから消しました");
        if (snapshot.IsFolder)
            parts.Add("フォルダを作成");
        if (snapshot.SizeVerified)
            parts.Add("サイズ照合OK");
        // 上書き・削除の前に控えを取ったか（「控えあり」など）
        if (snapshot.Note is { Length: > 0 } note)
            parts.Add(note);
        if (snapshot.FailedAttempts > 0)
            parts.Add($"{snapshot.FailedAttempts + 1}回目で成功");
        if (snapshot.FinishedAt is { } finishedAt)
            parts.Add(TimeZoneInfo.ConvertTime(finishedAt, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture));
        return string.Join("・", parts);
    }

    private static string FailedDetail(TransferItemSnapshot snapshot)
    {
        var reason = snapshot.LastFailure is { } failure ? DisplayText.Describe(failure) : "失敗しました";
        return snapshot.FailedAttempts > 1 ? $"{reason}・{snapshot.FailedAttempts}回試しました" : reason;
    }
}

/// <summary>転送の一覧に出すための、パスの切り出し。パソコン側は \ と / のどちらの区切りにも対応する。</summary>
internal static class TransferPaths
{
    public static string GetFileName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    public static string GetRemoteParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    public static string GetLocalParent(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        if (index < 0)
            return "";
        // C:\file.txt の親は C:\
        return index > 0 && trimmed[index - 1] == ':' ? trimmed[..(index + 1)] : trimmed[..index];
    }
}
