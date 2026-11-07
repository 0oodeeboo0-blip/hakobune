using Hakobune.Core.Text;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>前の区切りから後に終わったもの（転送がひと区切りついたときに、ログと通知で使う）。</summary>
/// <param name="Uploaded">送ったファイルの数。</param>
/// <param name="Downloaded">受け取ったファイルの数。</param>
/// <param name="Deleted">サーバーから消した数。</param>
/// <param name="Folders">作ったフォルダの数。</param>
/// <param name="Failed">失敗の数。</param>
/// <param name="UploadedBytes">送った大きさ。</param>
/// <param name="DownloadedBytes">受け取った大きさ。</param>
public sealed record TransferTotals(int Uploaded, int Downloaded, int Deleted, int Folders, int Failed, long UploadedBytes, long DownloadedBytes)
{
    /// <summary>何もない。</summary>
    public static TransferTotals None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>終わった数（失敗を除く）。</summary>
    public int Completed => Uploaded + Downloaded + Deleted + Folders;

    /// <summary>送った・受け取った大きさの合計。</summary>
    public long Bytes => UploadedBytes + DownloadedBytes;

    /// <summary>何も終わっていない（失敗も無い）。</summary>
    public bool IsEmpty => Completed + Failed == 0;
}

/// <summary>
/// 転送がひと区切りついた。<paramref name="Stopped"/> なら接続できずに止めた（<paramref name="Reason"/> はその理由）。
/// そうでなければ、送るものが無くなった。
/// </summary>
public sealed record TransferRunEnded(TransferTotals Totals, bool Stopped, TransferFailure? Reason = null);

/// <summary>転送がひと区切りついたときの知らせ（Windows の通知に出す）。</summary>
/// <param name="Title">見出し。</param>
/// <param name="Text">中身。</param>
/// <param name="IsProblem">失敗や、止まったことを知らせるか。</param>
public sealed record TransferNotice(string Title, string Text, bool IsProblem)
{
    /// <summary>知らせる文を作る。止まってもおらず、何も終わっていなければ null。</summary>
    public static TransferNotice? From(TransferRunEnded ended, string? connectionName)
    {
        ArgumentNullException.ThrowIfNull(ended);
        var name = string.IsNullOrWhiteSpace(connectionName) ? null : connectionName.Trim();
        var totals = ended.Totals;
        if (ended.Stopped)
        {
            var reason = ended.Reason is { } failure ? DisplayText.Describe(failure) : "理由は分かりません";
            return new TransferNotice(
                "転送を止めました",
                $"{(name is null ? "" : $"「{name}」に")}接続できないため、止めました（{reason}）。再開すると、続きから送ります",
                IsProblem: true);
        }
        if (totals.IsEmpty)
            return null;

        var done = Describe(totals);
        var prefix = name is null ? "" : name + ": ";
        if (totals.Failed > 0)
        {
            var failed = $"失敗した {totals.Failed:N0} 件は、転送の一覧の「失敗」で理由を見て、やり直せます";
            return new TransferNotice(
                $"転送が終わりました（失敗 {totals.Failed:N0} 件）",
                prefix + (done.Length > 0 ? $"{done}。{failed}" : failed),
                IsProblem: true);
        }
        var title = (totals.Uploaded > 0, totals.Downloaded > 0, totals.Deleted > 0) switch
        {
            (true, false, false) => "アップロードが終わりました",
            (false, true, false) => "ダウンロードが終わりました",
            (false, false, true) => "削除が終わりました",
            _ => "転送が終わりました",
        };
        return new TransferNotice(title, prefix + done, IsProblem: false);
    }

    // 例: 12 件を送りました（3.4 MB）、2 件をサーバーから消しました
    private static string Describe(TransferTotals totals)
    {
        var parts = new List<string>();
        if (totals.Uploaded > 0)
            parts.Add($"{totals.Uploaded:N0} 件を送りました（{DisplayText.Size(totals.UploadedBytes)}）");
        if (totals.Downloaded > 0)
            parts.Add($"{totals.Downloaded:N0} 件を受け取りました（{DisplayText.Size(totals.DownloadedBytes)}）");
        if (totals.Deleted > 0)
            parts.Add($"{totals.Deleted:N0} 件をサーバーから消しました");
        // フォルダは、ファイルを送っていないときだけ書く（空のフォルダだけを送ったときなど）
        if (totals.Folders > 0 && totals.Uploaded + totals.Downloaded == 0)
            parts.Add($"フォルダを {totals.Folders:N0} 個作りました");
        return string.Join("、", parts);
    }
}
