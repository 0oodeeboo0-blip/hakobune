using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;

namespace Hakobune.Presentation.Transfers;

/// <summary>同期の向き。</summary>
public enum SyncDirection
{
    /// <summary>パソコンのフォルダに合わせて、変わったファイルだけサーバーへ送る。</summary>
    Upload,

    /// <summary>サーバーのフォルダに合わせて、変わったファイルだけパソコンへ受け取る。</summary>
    Download,
}

/// <summary>比べてみて分かった違いの種類。</summary>
public enum SyncChange
{
    /// <summary>送り先に無い（空のフォルダなら、送り先に作る）。</summary>
    New,

    /// <summary>送るほうが新しい（日時が分からないときは、大きさが違う）。</summary>
    Changed,

    /// <summary>大きさが違うが、送り先のほうが新しい（送り先で直したのかもしれないので、既定では送らない）。</summary>
    TargetNewer,

    /// <summary>送り先にだけある（中身ごと 1 つに数える）。</summary>
    OnlyOnTarget,

    /// <summary>同じ名前で、片方はファイル、もう片方はフォルダ（送れない）。</summary>
    Conflict,
}

/// <summary>違い 1 つ。</summary>
/// <param name="Change">違いの種類。</param>
/// <param name="RelativePath">比べたフォルダからの位置（区切りは /）。</param>
/// <param name="Source">送るほうの項目（送り先にだけあるものは null）。</param>
/// <param name="Target">送り先の項目（無ければ null）。</param>
public sealed record SyncItem(SyncChange Change, string RelativePath, FileEntry? Source, FileEntry? Target)
{
    /// <summary>フォルダか。</summary>
    public bool IsDirectory => (Source ?? Target)?.IsDirectory ?? false;
}

/// <summary>比べた結果。</summary>
/// <param name="Direction">向き。</param>
/// <param name="SourceRoot">送るほうのフォルダ。</param>
/// <param name="TargetRoot">送り先のフォルダ。</param>
/// <param name="Items">違い（位置の順）。同じものは入れない。</param>
/// <param name="SameCount">同じだったファイルの数。</param>
/// <param name="ExcludedCount">送らないファイルの決まりで、比べなかった数（送るほう。フォルダは中身を数えず 1 つ）。</param>
/// <param name="UnreadableFolders">中を見られなかったフォルダ（その中は比べず、何もしない）。</param>
/// <param name="TooMany">多すぎて途中までしか比べなかったか。</param>
/// <param name="ClockOffset">送り先の日時に足して比べた時計のずれ（FTP の一覧の時刻がサーバーの地域の時刻のとき）。</param>
/// <param name="SkippedLinks">比べなかったリンクの数（送るほう）。</param>
public sealed record SyncPlan(
    SyncDirection Direction,
    string SourceRoot,
    string TargetRoot,
    IReadOnlyList<SyncItem> Items,
    int SameCount,
    int ExcludedCount,
    IReadOnlyList<string> UnreadableFolders,
    bool TooMany,
    TimeSpan ClockOffset,
    int SkippedLinks = 0)
{
    /// <summary>その種類の違いの数。</summary>
    public int Count(SyncChange change) => Items.Count(item => item.Change == change);
}

/// <summary>比べている途中の様子。</summary>
/// <param name="FoldersRead">読み終えたフォルダの数。</param>
/// <param name="FoldersKnown">いま分かっているフォルダの数（読んだもの＋これから読むもの）。</param>
public readonly record struct SyncScanProgress(int FoldersRead, int FoldersKnown);

/// <summary>
/// 同期の下準備。左右のフォルダを同時にたどって、名前・大きさ・更新日時で比べる（中身は読まない）。
/// 送らないファイルの決まりに当てはまるものは、どちら側でも比べない（送り先にだけあっても、消すものに入れない）。
/// リンクもたどらず、消さない。
/// </summary>
public static class SyncPlanner
{
    /// <summary>比べる項目の数の上限（うっかりドライブ全体を選んだときに止めるため）。</summary>
    public const int MaxEntries = 200_000;

    private static readonly TimeSpan SecondTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinuteTolerance = TimeSpan.FromSeconds(61);

    /// <summary>
    /// <paramref name="sourceRoot"/>（送るほう）と <paramref name="targetRoot"/>（送り先）を比べる。
    /// アップロードなら送るほうはパソコン、ダウンロードならサーバー。
    /// </summary>
    public static async Task<SyncPlan> PlanAsync(
        SyncDirection direction,
        IFileSource source,
        string sourceRoot,
        IFileSource target,
        string targetRoot,
        ExclusionRules exclusions,
        IProgress<SyncScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetRoot);
        ArgumentNullException.ThrowIfNull(exclusions);

        var upload = direction == SyncDirection.Upload;
        // サーバーの名前は大文字と小文字を区別し、パソコン（Windows）の名前は区別しない
        var comparer = upload ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        Func<string, string> targetName = upload ? name => name : TransferPlanner.ToLocalName;
        Func<string, string, string> combineTarget = upload ? RemotePath.Combine : Path.Combine;

        var items = new List<SyncItem>();
        var pairs = new List<(string Relative, FileEntry Source, FileEntry Target)>();
        var unreadable = new List<string>();
        var excluded = 0;
        var links = 0;
        var entries = 0;
        var tooMany = false;
        var foldersRead = 0;

        var pending = new Stack<Folder>();
        pending.Push(new Folder("", null, sourceRoot, targetRoot, TargetExists: true));
        while (!tooMany && pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceListing = await source.ListAsync(folder.SourcePath, cancellationToken).ConfigureAwait(false);
            if (sourceListing.Failure is { } sourceFailure)
            {
                // いちばん上のフォルダを見られなければ、比べようがない
                if (folder.Relative.Length == 0)
                    throw new RemoteOperationException(sourceFailure);
                unreadable.Add(folder.SourcePath);
                continue;
            }
            IReadOnlyList<FileEntry> targetEntries = [];
            if (folder.TargetExists)
            {
                var targetListing = await target.ListAsync(folder.TargetPath, cancellationToken).ConfigureAwait(false);
                if (targetListing.Failure is { } targetFailure)
                {
                    if (folder.Relative.Length == 0)
                        throw new RemoteOperationException(targetFailure);
                    // 送り先を見られないフォルダは、比べられないので何もしない
                    unreadable.Add(folder.TargetPath);
                    continue;
                }
                targetEntries = targetListing.Entries;
            }
            foldersRead++;
            progress?.Report(new SyncScanProgress(foldersRead, foldersRead + pending.Count));

            var targets = new Dictionary<string, FileEntry>(comparer);
            foreach (var entry in targetEntries)
            {
                if (entry.Type == FileEntryType.Drive)
                    continue;
                // 送らない名前は、送り先にあっても比べず、消しもしない
                if (exclusions.MatchLast(Join(folder.Relative, entry.Name), entry.IsDirectory) is not null)
                    continue;
                targets.TryAdd(entry.Name, entry);
            }

            var sourceCount = 0;
            var sentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in sourceListing.Entries.OrderBy(entry => entry.Name, NaturalStringComparer.Instance))
            {
                if (entry.Type == FileEntryType.Drive)
                    continue;
                var relative = Join(folder.Relative, entry.Name);
                if (exclusions.MatchLast(relative, entry.IsDirectory) is not null)
                {
                    excluded++;
                    continue;
                }
                var name = targetName(entry.Name);
                sentNames.Add(name);
                // リンク（パソコンのジャンクション、サーバーのシンボリックリンク）は、先がファイルかフォルダか分からず、
                // 大きさも当てにならないので比べない。送り先の同じ名前のものもそのままにする
                if (entry.Type == FileEntryType.Link)
                {
                    targets.Remove(name);
                    links++;
                    continue;
                }
                if (++entries > MaxEntries)
                {
                    tooMany = true;
                    break;
                }
                sourceCount++;
                targets.Remove(name, out var other);
                if (other is null)
                {
                    if (entry.IsDirectory)
                        pending.Push(new Folder(relative, entry, entry.FullPath, combineTarget(folder.TargetPath, name), TargetExists: false));
                    else
                        items.Add(new SyncItem(SyncChange.New, relative, entry, null));
                }
                else if (entry.IsDirectory && other.Type == FileEntryType.Directory)
                {
                    pending.Push(new Folder(relative, entry, entry.FullPath, other.FullPath, TargetExists: true));
                }
                else if (entry.IsDirectory || other.Type != FileEntryType.File)
                {
                    // ファイルとフォルダ（またはリンク）で種類が違う
                    items.Add(new SyncItem(SyncChange.Conflict, relative, entry, other));
                }
                else
                {
                    pairs.Add((relative, entry, other));
                }
            }

            // 送り先に無い空のフォルダは、送り先にも作る
            if (!tooMany && sourceCount == 0 && !folder.TargetExists && folder.Entry is not null)
                items.Add(new SyncItem(SyncChange.New, folder.Relative, folder.Entry, null));
            if (!tooMany)
            {
                foreach (var entry in targets.Values)
                {
                    // 大文字と小文字だけが違う名前（Index.html と index.html）は、大文字と小文字を区別しないサーバーでは
                    // 送ったものと同じファイルなので、消すものには入れない。リンクも消さない
                    if ((upload && sentNames.Contains(entry.Name)) || entry.Type == FileEntryType.Link)
                        continue;
                    items.Add(new SyncItem(SyncChange.OnlyOnTarget, Join(folder.Relative, entry.Name), null, entry));
                }
            }
        }

        var offset = DetectClockOffset(pairs.Select(pair => (pair.Source, pair.Target)));
        var same = 0;
        foreach (var (relative, sourceEntry, targetEntry) in pairs)
        {
            if (Compare(sourceEntry, targetEntry, offset) is { } change)
                items.Add(new SyncItem(change, relative, sourceEntry, targetEntry));
            else
                same++;
        }
        items.Sort((x, y) => NaturalStringComparer.Instance.Compare(x.RelativePath, y.RelativePath));
        return new SyncPlan(direction, sourceRoot, targetRoot, items, same, excluded, unreadable, tooMany, offset, links);
    }

    /// <summary>
    /// 選んだ違いを、送信待ちに入れる依頼にする。送るのは新しいものと変わったもの
    /// （<paramref name="includeTargetNewer"/> なら送り先のほうが新しいものも）。
    /// <paramref name="deleteOnlyOnTarget"/> なら、サーバーにだけあるものを消す依頼も足す（アップロードのときだけ。消すのは最後）。
    /// </summary>
    public static IReadOnlyList<TransferRequest> ToRequests(SyncPlan plan, bool includeTargetNewer, bool deleteOnlyOnTarget)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var requests = new List<TransferRequest>();
        var deletes = new List<TransferRequest>();
        foreach (var item in plan.Items)
        {
            switch (item.Change)
            {
                case SyncChange.New:
                case SyncChange.Changed:
                case SyncChange.TargetNewer when includeTargetNewer:
                    requests.Add(ToTransfer(plan, item));
                    break;
                case SyncChange.OnlyOnTarget when deleteOnlyOnTarget && plan.Direction == SyncDirection.Upload && item.Target is { } target:
                    deletes.Add(new TransferRequest(TransferDirection.Upload, "", target.FullPath, IsDelete: true));
                    break;
            }
        }
        requests.AddRange(deletes);
        return requests;
    }

    /// <summary>
    /// 2 つのファイルを比べる。同じなら null。<paramref name="clockOffset"/> は送り先の日時に足すずれ。
    /// 日時の細かさがサーバーによって違う（分まで・日付まで）ので、そのぶんは同じとみなす。
    /// </summary>
    public static SyncChange? Compare(FileEntry source, FileEntry target, TimeSpan clockOffset)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var sizeDiffers = source.Size != target.Size;
        // 日時が分からない（または日付までしか分からない）ときは、大きさだけで比べる
        if (source.Modified is not { } sourceTime || target.Modified is not { } targetTime || IsDateOnly(sourceTime) || IsDateOnly(targetTime))
            return sizeDiffers ? SyncChange.Changed : null;

        var tolerance = HasMinutePrecision(sourceTime) || HasMinutePrecision(targetTime) ? MinuteTolerance : SecondTolerance;
        var sourceNewerBy = sourceTime - (targetTime + clockOffset);
        if (!sizeDiffers)
            return sourceNewerBy > tolerance ? SyncChange.Changed : null;
        return -sourceNewerBy > tolerance ? SyncChange.TargetNewer : SyncChange.Changed;
    }

    /// <summary>
    /// 時計のずれを見つける。大きさが同じファイルの日時の差が、半分より多くで同じ時差（15 分刻み）なら、それをずれとみなす
    /// （FTP の LIST の時刻は、サーバーの地域の時刻で届くため）。見つからなければ 0。
    /// </summary>
    public static TimeSpan DetectClockOffset(IEnumerable<(FileEntry Source, FileEntry Target)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var counts = new Dictionary<int, int>();
        var total = 0;
        foreach (var (source, target) in pairs)
        {
            if (source.Size != target.Size || source.Modified is not { } sourceTime || target.Modified is not { } targetTime
                || IsDateOnly(sourceTime) || IsDateOnly(targetTime))
                continue;
            total++;
            var minutes = (int)Math.Round((sourceTime - targetTime).TotalMinutes);
            counts[minutes] = counts.GetValueOrDefault(minutes) + 1;
        }
        if (total < 3)
            return TimeSpan.Zero;
        var best = counts.MaxBy(pair => pair.Value);
        return best.Key != 0 && best.Key % 15 == 0 && Math.Abs(best.Key) <= 14 * 60 && best.Value * 2 > total
            ? TimeSpan.FromMinutes(best.Key)
            : TimeSpan.Zero;
    }

    private static TransferRequest ToTransfer(SyncPlan plan, SyncItem item)
    {
        var source = item.Source ?? throw new InvalidOperationException("送るほうの項目がありません。");
        var size = source.IsDirectory ? 0 : source.Size;
        if (plan.Direction == SyncDirection.Upload)
        {
            var remote = item.Target?.FullPath ?? CombineRemote(plan.TargetRoot, item.RelativePath);
            return new TransferRequest(TransferDirection.Upload, source.FullPath, remote, ConflictPolicy.Overwrite, size, IsFolder: source.IsDirectory);
        }
        var local = item.Target?.FullPath ?? CombineLocal(plan.TargetRoot, item.RelativePath);
        return new TransferRequest(TransferDirection.Download, local, source.FullPath, ConflictPolicy.Overwrite, size, IsFolder: source.IsDirectory);
    }

    private static string CombineRemote(string root, string relative) =>
        relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(root, RemotePath.Combine);

    private static string CombineLocal(string root, string relative) =>
        relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(root, (folder, name) => Path.Combine(folder, TransferPlanner.ToLocalName(name)));

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    // 秒がちょうど 0 の時刻は、分までしか分からないのかもしれない（FTP の LIST）
    private static bool HasMinutePrecision(DateTimeOffset time) => time.Ticks % TimeSpan.TicksPerMinute == 0;

    // 0 時ちょうどの時刻は、日付までしか分からないのかもしれない（FTP の LIST で、半年より前のファイル）
    private static bool IsDateOnly(DateTimeOffset time) => time.Ticks % TimeSpan.TicksPerDay == 0;

    private sealed record Folder(string Relative, FileEntry? Entry, string SourcePath, string TargetPath, bool TargetExists);
}
