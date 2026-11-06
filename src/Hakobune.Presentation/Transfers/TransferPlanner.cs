using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;

namespace Hakobune.Presentation.Transfers;

/// <summary>選んだファイルとフォルダから作った、転送の依頼。</summary>
/// <param name="Requests">送る（受け取る）ファイルと、空のフォルダ。</param>
/// <param name="UnreadableFolders">中を見られなかったフォルダ。</param>
/// <param name="TooMany">多すぎて途中までにしたか。</param>
public sealed record TransferPlan(IReadOnlyList<TransferRequest> Requests, IReadOnlyList<string> UnreadableFolders, bool TooMany);

/// <summary>
/// 選んだファイルとフォルダから、転送の依頼を作る。フォルダは中をたどり、同じ形で送り先に作る
/// （中身のあるフォルダはファイルを送るときに作られるので、空のフォルダだけ「フォルダを作る」依頼にする）。
/// 一覧は画面と同じもの（<see cref="IFileSource"/>）で取るので、隠しファイルなど画面に出ないものは送らない。
/// </summary>
public static class TransferPlanner
{
    /// <summary>一度に送れるファイルの数の上限（うっかりドライブ全体を選んだときに止めるため）。</summary>
    public const int MaxFiles = 100_000;

    /// <summary>パソコンの項目を、サーバーの <paramref name="remoteFolder"/> へ送る依頼にする。</summary>
    public static Task<TransferPlan> PlanUploadAsync(
        IFileSource local, IEnumerable<FileEntry> selected, string remoteFolder, ConflictPolicy onConflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(remoteFolder);
        return PlanAsync(
            local,
            selected,
            remoteFolder,
            RemotePath.Combine,
            (entry, target) => new TransferRequest(TransferDirection.Upload, entry.FullPath, target, onConflict, entry.Size),
            (entry, target) => new TransferRequest(TransferDirection.Upload, entry.FullPath, target, onConflict, 0, IsFolder: true),
            // パソコン側のリンク（ジャンクションなど）はたどらない（同じ場所を何度も回らないように）
            followLinks: false,
            cancellationToken);
    }

    /// <summary>サーバーの項目を、パソコンの <paramref name="localFolder"/> へ受け取る依頼にする。</summary>
    public static Task<TransferPlan> PlanDownloadAsync(
        IFileSource remote, IEnumerable<FileEntry> selected, string localFolder, ConflictPolicy onConflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentException.ThrowIfNullOrEmpty(localFolder);
        return PlanAsync(
            remote,
            selected,
            localFolder,
            (folder, name) => Path.Combine(folder, ToLocalName(name)),
            (entry, target) => new TransferRequest(TransferDirection.Download, target, entry.FullPath, onConflict, entry.Size),
            (entry, target) => new TransferRequest(TransferDirection.Download, target, entry.FullPath, onConflict, 0, IsFolder: true),
            // サーバーのリンクは、たいていファイルを指すので、ファイルとして受け取る
            followLinks: true,
            cancellationToken);
    }

    /// <summary>
    /// サーバーのファイル名を、Windows で使える名前にする（使えない文字は「_」にし、終わりの点と空白は取る）。
    /// </summary>
    public static string ToLocalName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < ' ' || chars[i] is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                chars[i] = '_';
        }
        var result = new string(chars).TrimEnd('.', ' ');
        return result.Length == 0 ? "_" : result;
    }

    private static async Task<TransferPlan> PlanAsync(
        IFileSource source,
        IEnumerable<FileEntry> selected,
        string destination,
        Func<string, string, string> combine,
        Func<FileEntry, string, TransferRequest> request,
        Func<FileEntry, string, TransferRequest> folderRequest,
        bool followLinks,
        CancellationToken cancellationToken)
    {
        var requests = new List<TransferRequest>();
        var unreadable = new List<string>();
        // 深いフォルダでも呼び出しが深くならないよう、自分で積んでたどる。選んだ順に並ぶよう、逆から積む
        var pending = new Stack<(FileEntry Entry, string Target)>();
        foreach (var entry in selected.Reverse())
        {
            if (entry.Type != FileEntryType.Drive)
                pending.Push((entry, combine(destination, entry.Name)));
        }

        while (pending.TryPop(out var next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entry, target) = next;
            switch (entry.Type)
            {
                case FileEntryType.File:
                case FileEntryType.Link when followLinks:
                    if (requests.Count >= MaxFiles)
                        return new TransferPlan(requests, unreadable, TooMany: true);
                    requests.Add(request(entry, target));
                    break;

                case FileEntryType.Directory:
                    var listing = await source.ListAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
                    if (!listing.Succeeded)
                    {
                        unreadable.Add(entry.FullPath);
                        break;
                    }
                    if (listing.Entries.Count == 0)
                    {
                        // 空のフォルダも、送り先に作る
                        if (requests.Count >= MaxFiles)
                            return new TransferPlan(requests, unreadable, TooMany: true);
                        requests.Add(folderRequest(entry, target));
                        break;
                    }
                    // 名前の順に送る（一覧で見た順と同じに）
                    foreach (var child in listing.Entries.OrderByDescending(child => child.Name, NaturalStringComparer.Instance))
                        pending.Push((child, combine(target, child.Name)));
                    break;
            }
        }
        return new TransferPlan(requests, unreadable, TooMany: false);
    }
}
