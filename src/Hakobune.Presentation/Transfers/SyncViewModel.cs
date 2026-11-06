using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Hakobune.Core.Text;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>同期の確認の画面に出すもの（画面がダイアログで出す）。</summary>
public interface ISyncDialogs
{
    /// <summary>
    /// 比べている様子と結果を出し、送るかどうかを聞く。送るなら true。
    /// 比べるのは呼び出し側が始めておく（<see cref="SyncViewModel.Start"/>）。やめたときは呼び出し側が止める。
    /// </summary>
    Task<bool> ReviewSyncAsync(SyncViewModel sync);
}

/// <summary>同期の確認の画面の 1 行。</summary>
/// <param name="Chip">印（新規・更新など）。</param>
/// <param name="Kind">印の色分け。</param>
/// <param name="Path">比べたフォルダからの位置。</param>
/// <param name="SizeText">大きさ（フォルダは空）。</param>
/// <param name="WhenText">日時（変わったものは、相手側の日時も）。</param>
/// <param name="Deletes">サーバーから消すものか。</param>
public sealed record SyncRow(string Chip, SyncChange Kind, string Path, string SizeText, string WhenText, bool Deletes = false)
{
    /// <summary>印を「新規」の色で出すか。</summary>
    public bool IsNew => Kind == SyncChange.New;

    /// <summary>印を「更新」の色で出すか（送り先のほうが新しいものも同じ色）。</summary>
    public bool IsChanged => Kind is SyncChange.Changed or SyncChange.TargetNewer;

    /// <summary>印を「消す」の色で出すか。</summary>
    public bool IsDelete => Deletes;

    /// <summary>印を灰色で出すか（残すもの、送れないもの）。</summary>
    public bool IsOther => !Deletes && Kind is SyncChange.OnlyOnTarget or SyncChange.Conflict;
}

/// <summary>
/// 同期の確認の画面。左右のフォルダを比べている様子と、何を送る（消す）かを見せる。
/// </summary>
public sealed class SyncViewModel : ObservableObject
{
    private readonly Func<IProgress<SyncScanProgress>, CancellationToken, Task<SyncPlan>> _plan;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TimeProvider _time;
    private SyncPlan? _result;

    /// <summary>
    /// <paramref name="plan"/> は比べる処理（<see cref="SyncPlanner.PlanAsync"/> を呼ぶ）。
    /// <paramref name="sourceText"/> と <paramref name="targetText"/> は画面に出す送るほうと送り先。
    /// </summary>
    public SyncViewModel(
        SyncDirection direction,
        string sourceText,
        string targetText,
        Func<IProgress<SyncScanProgress>, CancellationToken, Task<SyncPlan>> plan,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(targetText);
        ArgumentNullException.ThrowIfNull(plan);
        Direction = direction;
        SourceText = sourceText;
        TargetText = targetText;
        _plan = plan;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>向き。</summary>
    public SyncDirection Direction { get; }

    /// <summary>アップロードの向きか。</summary>
    public bool IsUpload => Direction == SyncDirection.Upload;

    /// <summary>画面の見出し。</summary>
    public string Title => IsUpload ? "変わったファイルだけ送る" : "変わったファイルだけ受け取る";

    /// <summary>送るほう（パソコンかサーバーのフォルダ）。</summary>
    public string SourceText { get; }

    /// <summary>送り先。</summary>
    public string TargetText { get; }

    /// <summary>送るほうの見出し（「パソコン」か「サーバー」）。</summary>
    public string SourceLabel => IsUpload ? "パソコン（送るほう）" : "サーバー（送るほう）";

    /// <summary>送り先の見出し。</summary>
    public string TargetLabel => IsUpload ? "サーバー（送り先）" : "パソコン（受け取る先）";

    /// <summary>送り先のほうが新しいものの説明。</summary>
    public string TargetNewerHint => IsUpload
        ? "サーバーで直したのかもしれないので、そのままにします。"
        : "パソコンで直したのかもしれないので、そのままにします。";

    /// <summary>送り先のほうが新しいものも送るかの欄の文。</summary>
    public string IncludeTargetNewerText => IsUpload
        ? "これも送る（サーバーのファイルを上書きします）"
        : "これも受け取る（パソコンのファイルを上書きします）";

    /// <summary>送るものが無いときの文。</summary>
    public string NothingToSendText => IsUpload
        ? "送るファイルはありません。変わったものはありませんでした。"
        : "受け取るファイルはありません。変わったものはありませんでした。";

    /// <summary>比べている途中か。</summary>
    public bool IsScanning { get; private set => SetProperty(ref field, value); }

    /// <summary>比べ終わって、結果を出せるか。</summary>
    public bool HasResult { get; private set => SetProperty(ref field, value); }

    /// <summary>比べている様子（「フォルダを読んで比べています（18 / 42）」）。</summary>
    public string ScanText { get; private set => SetProperty(ref field, value); } = "比べる準備をしています…";

    /// <summary>比べられなかったときの理由（なければ空）。</summary>
    public string ErrorText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>比べられなかったか。</summary>
    public bool HasError => ErrorText.Length > 0;

    /// <summary>送る（受け取る）もの。</summary>
    public IReadOnlyList<SyncRow> SendRows { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>大きさが違い、送り先のほうが新しいもの。</summary>
    public IReadOnlyList<SyncRow> TargetNewerRows { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>送り先にだけあるもの。</summary>
    public IReadOnlyList<SyncRow> OnlyOnTargetRows { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>同じ名前でファイルとフォルダが違うもの（送れない）。</summary>
    public IReadOnlyList<SyncRow> ConflictRows { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>まとめの数（「新しい 2」など）。</summary>
    public string NewText { get; private set => SetProperty(ref field, value); } = "";

    public string ChangedText { get; private set => SetProperty(ref field, value); } = "";

    public string TargetNewerText { get; private set => SetProperty(ref field, value); } = "";

    public string OnlyOnTargetText { get; private set => SetProperty(ref field, value); } = "";

    public string SameText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>送るものの見出し（「送るファイル（5）」）。</summary>
    public string SendHeader { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>送り先のほうが新しいものの見出し。</summary>
    public string TargetNewerHeader { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>送り先にだけあるものの見出し。</summary>
    public string OnlyOnTargetHeader { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>送れないものの説明。</summary>
    public string ConflictHeader { get; private set => SetProperty(ref field, value); } = "";

    public bool HasSendRows => SendRows.Count > 0;

    public bool HasTargetNewerRows => TargetNewerRows.Count > 0;

    public bool HasOnlyOnTargetRows => OnlyOnTargetRows.Count > 0;

    public bool HasConflictRows => ConflictRows.Count > 0;

    /// <summary>比べ終わって、送る（受け取る）ものが無いか。</summary>
    public bool ShowsNothingToSend => HasResult && SendRows.Count == 0;

    /// <summary>送り先のほうが新しいものも送るか（既定では送らない）。</summary>
    public bool IncludeTargetNewer
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                UpdateAction();
        }
    }

    /// <summary>サーバーにだけあるものを消すか（アップロードのときだけ。既定では消さない）。</summary>
    public bool DeleteOnlyOnTarget
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;
            OnPropertyChanged(nameof(ShowsDeleteWarning));
            OnPropertyChanged(nameof(ShowsDeleteBackupNote));
            // 印（残す・消す）を付け直す
            if (_result is { } result)
                OnlyOnTargetRows = [.. result.Items.Where(item => item.Change == SyncChange.OnlyOnTarget).Select(item => ToRow(item, TargetSide))];
            UpdateAction();
        }
    }

    /// <summary>送り先にだけあるものを消せるか（アップロードのときだけ）。</summary>
    public bool CanDeleteOnlyOnTarget => IsUpload && HasOnlyOnTargetRows;

    /// <summary>上書き・削除の前に控えを取るか（設定の「控え」）。</summary>
    public bool BacksUp { get; init; }

    /// <summary>消すことを選んだが、控えを取らないので元に戻せないか（赤い注意を出す）。</summary>
    public bool ShowsDeleteWarning => DeleteOnlyOnTarget && !BacksUp;

    /// <summary>消すことを選び、消す前に控えを取るか（そう伝える）。</summary>
    public bool ShowsDeleteBackupNote => DeleteOnlyOnTarget && BacksUp;

    /// <summary>決定のボタンの文（「5 件を送る」「5 件を送り、2 件を消す」）。</summary>
    public string PrimaryText { get; private set => SetProperty(ref field, value); } = "比べています…";

    /// <summary>決定できるか（送るか消すものがある）。</summary>
    public bool CanConfirm { get; private set => SetProperty(ref field, value); }

    /// <summary>補足（送らないファイル・時計のずれ・見られなかったフォルダ）。</summary>
    public IReadOnlyList<string> Notes { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>補足があるか。</summary>
    public bool HasNotes => Notes.Count > 0;

    /// <summary>比べ終わるまでの Task（始める前は終わったもの）。</summary>
    public Task Planning { get; private set; } = Task.CompletedTask;

    /// <summary>比べた結果（比べ終わるまでは null）。</summary>
    public SyncPlan? Result => _result;

    /// <summary>比べ始める（2 回目からは何もしない）。</summary>
    public void Start()
    {
        if (IsScanning || HasResult || HasError)
            return;
        IsScanning = true;
        var progress = new Progress<SyncScanProgress>(OnProgress);
        Planning = RunAsync(progress);
    }

    /// <summary>比べるのをやめる。</summary>
    public void Cancel() => _cancel.Cancel();

    /// <summary>選んだとおりの依頼にする（比べ終わる前は空）。</summary>
    public IReadOnlyList<TransferRequest> BuildRequests() =>
        _result is null ? [] : SyncPlanner.ToRequests(_result, IncludeTargetNewer, IsUpload && DeleteOnlyOnTarget);

    private async Task RunAsync(IProgress<SyncScanProgress> progress)
    {
        try
        {
            var result = await _plan(progress, _cancel.Token).ConfigureAwait(true);
            _result = result;
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            ErrorText = "やめました";
            OnPropertyChanged(nameof(HasError));
        }
        catch (Exception ex)
        {
            ErrorText = "比べられませんでした: " + DisplayText.Describe(FailureClassifier.FromException(ex));
            OnPropertyChanged(nameof(HasError));
            PrimaryText = "送るものはありません";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void OnProgress(SyncScanProgress value)
    {
        if (!IsScanning)
            return;
        var known = Math.Max(value.FoldersKnown, value.FoldersRead);
        ScanText = $"フォルダを読んで比べています（{value.FoldersRead:N0} / {known:N0}）";
    }

    private string TargetSide => IsUpload ? "サーバー" : "パソコン";

    private void ShowResult(SyncPlan plan)
    {
        var upload = IsUpload;
        var targetSide = TargetSide;
        SendRows = [.. plan.Items.Where(item => item.Change is SyncChange.New or SyncChange.Changed).Select(item => ToRow(item, targetSide))];
        TargetNewerRows = [.. plan.Items.Where(item => item.Change == SyncChange.TargetNewer).Select(item => ToRow(item, targetSide))];
        OnlyOnTargetRows = [.. plan.Items.Where(item => item.Change == SyncChange.OnlyOnTarget).Select(item => ToRow(item, targetSide))];
        ConflictRows = [.. plan.Items.Where(item => item.Change == SyncChange.Conflict).Select(item => ToRow(item, targetSide))];

        var newCount = plan.Count(SyncChange.New);
        var changedCount = plan.Count(SyncChange.Changed);
        NewText = $"新しい {newCount:N0}";
        ChangedText = $"変わった {changedCount:N0}";
        TargetNewerText = TargetNewerRows.Count > 0 ? $"{targetSide}のほうが新しい {TargetNewerRows.Count:N0}" : "";
        OnlyOnTargetText = $"{targetSide}にだけある {OnlyOnTargetRows.Count:N0}";
        SameText = $"同じ {plan.SameCount:N0}（{(upload ? "送りません" : "受け取りません")}）";
        SendHeader = $"{(upload ? "送るファイル" : "受け取るファイル")}（{SendRows.Count:N0}）";
        TargetNewerHeader = $"大きさが違い、{targetSide}のほうが新しいファイル（{TargetNewerRows.Count:N0}）";
        OnlyOnTargetHeader = upload
            ? $"サーバーにだけあるもの（{OnlyOnTargetRows.Count:N0}）"
            : $"パソコンにだけあるもの（{OnlyOnTargetRows.Count:N0}）。そのままにします";
        ConflictHeader = $"名前は同じで種類が違うもの（{ConflictRows.Count:N0}）。ファイルとフォルダ（またはリンク）なので、{(upload ? "送りません" : "受け取りません")}";

        var notes = new List<string>();
        if (BacksUp && plan.Items.Any(item => item.Change is SyncChange.Changed or SyncChange.TargetNewer))
            notes.Add($"上書きする前に、{targetSide}のいまのファイルの控えを取ります（下の一覧の「控え」から元に戻せます）。");
        if (plan.ExcludedCount > 0)
            notes.Add($"送らないファイル（Thumbs.db など）{plan.ExcludedCount:N0} 件は比べていません。");
        if (plan.SkippedLinks > 0)
            notes.Add($"リンク {plan.SkippedLinks:N0} 個は、先が分からないので比べていません。");
        if (plan.ClockOffset != TimeSpan.Zero)
            notes.Add($"サーバーの一覧の時刻が {DescribeOffset(plan.ClockOffset)} ずれていたので、そのぶん直して比べました。");
        if (plan.UnreadableFolders.Count > 0)
            notes.Add($"中を見られないフォルダ {plan.UnreadableFolders.Count:N0} 個は比べていません（{string.Join("、", plan.UnreadableFolders.Take(2))}{(plan.UnreadableFolders.Count > 2 ? " ほか" : "")}）。");
        if (plan.TooMany)
            notes.Add($"多すぎるので、最初の {SyncPlanner.MaxEntries:N0} 件だけ比べました。");
        Notes = notes;

        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasSendRows));
        OnPropertyChanged(nameof(HasTargetNewerRows));
        OnPropertyChanged(nameof(HasOnlyOnTargetRows));
        OnPropertyChanged(nameof(HasConflictRows));
        OnPropertyChanged(nameof(CanDeleteOnlyOnTarget));
        HasResult = true;
        OnPropertyChanged(nameof(ShowsNothingToSend));
        UpdateAction();
    }

    private void UpdateAction()
    {
        if (!HasResult)
            return;
        var send = SendRows.Count + (IncludeTargetNewer ? TargetNewerRows.Count : 0);
        var delete = IsUpload && DeleteOnlyOnTarget ? OnlyOnTargetRows.Count : 0;
        var verb = IsUpload ? "送る" : "受け取る";
        PrimaryText = (send, delete) switch
        {
            (0, 0) => IsUpload ? "送るものはありません" : "受け取るものはありません",
            (_, 0) => $"{send:N0} 件を{verb}",
            (0, _) => $"{delete:N0} 件を消す",
            _ => $"{send:N0} 件を送り、{delete:N0} 件を消す",
        };
        CanConfirm = send + delete > 0;
    }

    private SyncRow ToRow(SyncItem item, string targetSide)
    {
        var source = item.Source;
        var target = item.Target;
        var entry = source ?? target!;
        var size = entry.IsDirectory ? "" : DisplayText.FileSize(entry.Size);
        var path = item.IsDirectory ? item.RelativePath + "/" : item.RelativePath;
        return item.Change switch
        {
            SyncChange.New => new SyncRow(entry.IsDirectory ? "フォルダ" : "新規", item.Change, path, size, Format(source?.Modified)),
            SyncChange.Changed or SyncChange.TargetNewer =>
                new SyncRow("更新", item.Change, path, size, $"{Format(source?.Modified)}（{targetSide} {Format(target?.Modified)}）"),
            SyncChange.OnlyOnTarget when IsUpload && DeleteOnlyOnTarget =>
                new SyncRow("消す", item.Change, path, size, Format(target?.Modified), Deletes: true),
            SyncChange.OnlyOnTarget => new SyncRow("残す", item.Change, path, size, Format(target?.Modified)),
            _ => new SyncRow("種類違い", item.Change, path, size, ""),
        };
    }

    // 今年なら月日と時刻、去年より前なら年も出す
    private string Format(DateTimeOffset? time)
    {
        if (time is not { } value)
            return "";
        var zone = _time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(value, zone);
        var thisYear = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).Year;
        return local.ToString(local.Year == thisYear ? "MM/dd HH:mm" : "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string DescribeOffset(TimeSpan offset)
    {
        var minutes = (int)Math.Abs(offset.TotalMinutes);
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} 時間" : hours == 0 ? $"{rest} 分" : $"{hours} 時間 {rest} 分";
    }
}
