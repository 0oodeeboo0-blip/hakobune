using Hakobune.Core.Text;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Tests.Transfers;

/// <summary>転送がひと区切りついたときの、Windows の通知の文。</summary>
public sealed class TransferNoticeTests
{
    private static TransferRunEnded Done(
        int uploaded = 0, int downloaded = 0, int deleted = 0, int folders = 0, int failed = 0, long uploadedBytes = 0, long downloadedBytes = 0) =>
        new(new TransferTotals(uploaded, downloaded, deleted, folders, failed, uploadedBytes, downloadedBytes), Stopped: false);

    [Fact]
    public void Uploads_say_how_many_files_and_how_much_was_sent()
    {
        var notice = TransferNotice.From(Done(uploaded: 12, folders: 2, uploadedBytes: 3_456_789), "ショップ（本番）");

        Assert.NotNull(notice);
        Assert.Equal("アップロードが終わりました", notice.Title);
        // フォルダは、ファイルを送ったときは数えない
        Assert.Equal($"ショップ（本番）: 12 件を送りました（{DisplayText.Size(3_456_789)}）", notice.Text);
        Assert.False(notice.IsProblem);
    }

    [Fact]
    public void Downloads_deletes_and_mixed_runs_have_their_own_titles()
    {
        Assert.Equal("ダウンロードが終わりました", TransferNotice.From(Done(downloaded: 3, downloadedBytes: 2048), null)?.Title);
        Assert.Equal($"3 件を受け取りました（{DisplayText.Size(2048)}）", TransferNotice.From(Done(downloaded: 3, downloadedBytes: 2048), null)?.Text);
        Assert.Equal("削除が終わりました", TransferNotice.From(Done(deleted: 2), "ブログ")?.Title);
        Assert.Equal("ブログ: 2 件をサーバーから消しました", TransferNotice.From(Done(deleted: 2), "ブログ")?.Text);

        // 同期（送って、サーバーにだけあるものを消す）
        var sync = TransferNotice.From(Done(uploaded: 5, deleted: 1, uploadedBytes: 500), "ショップ");
        Assert.Equal("転送が終わりました", sync?.Title);
        Assert.Equal("ショップ: 5 件を送りました（500 バイト）、1 件をサーバーから消しました", sync?.Text);

        // 空のフォルダだけを送った
        Assert.Equal("ショップ: フォルダを 2 個作りました", TransferNotice.From(Done(folders: 2), "ショップ")?.Text);
    }

    [Fact]
    public void Failures_are_counted_and_point_to_where_to_retry()
    {
        var notice = TransferNotice.From(Done(uploaded: 10, failed: 2, uploadedBytes: 100), "ショップ");

        Assert.NotNull(notice);
        Assert.Equal("転送が終わりました（失敗 2 件）", notice.Title);
        Assert.Equal("ショップ: 10 件を送りました（100 バイト）。失敗した 2 件は、転送の一覧の「失敗」で理由を見て、やり直せます", notice.Text);
        Assert.True(notice.IsProblem);

        // 全部失敗した
        Assert.Equal(
            "ショップ: 失敗した 1 件は、転送の一覧の「失敗」で理由を見て、やり直せます",
            TransferNotice.From(Done(failed: 1), "ショップ")?.Text);
    }

    [Fact]
    public void Stopping_because_the_server_cannot_be_reached_gives_the_reason()
    {
        var failure = TransferFailure.Permanent(FailureReason.AuthenticationFailed);
        var notice = TransferNotice.From(new TransferRunEnded(TransferTotals.None, Stopped: true, failure), "ショップ（本番）");

        Assert.NotNull(notice);
        Assert.Equal("転送を止めました", notice.Title);
        Assert.Equal($"「ショップ（本番）」に接続できないため、止めました（{DisplayText.Describe(failure)}）。再開すると、続きから送ります", notice.Text);
        Assert.True(notice.IsProblem);
    }

    [Fact]
    public void Nothing_to_tell_when_nothing_finished()
    {
        Assert.Null(TransferNotice.From(Done(), "ショップ"));
    }
}
