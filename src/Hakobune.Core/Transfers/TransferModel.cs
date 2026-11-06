namespace Hakobune.Core.Transfers;

/// <summary>転送の向き。</summary>
public enum TransferDirection
{
    /// <summary>パソコンからサーバーへ。</summary>
    Upload,

    /// <summary>サーバーからパソコンへ。</summary>
    Download,
}

/// <summary>送信待ちの 1 件の状態。</summary>
public enum TransferState
{
    /// <summary>待機中。</summary>
    Queued,

    /// <summary>転送中。</summary>
    Running,

    /// <summary>一時的な失敗のあと、やり直しを待っている。</summary>
    WaitingToRetry,

    /// <summary>完了。</summary>
    Completed,

    /// <summary>失敗（やり直しても変わらない、またはやり直しの上限に達した）。</summary>
    Failed,

    /// <summary>同じ名前のファイルの扱いなどにより、送らなかった。</summary>
    Skipped,

    /// <summary>利用者が取り消した。</summary>
    Cancelled,
}

/// <summary>転送先に同じ名前のファイルがあるときの扱い。</summary>
public enum ConflictPolicy
{
    /// <summary>上書きする。</summary>
    Overwrite,

    /// <summary>送るほうが新しいときだけ上書きする。</summary>
    OverwriteIfNewer,

    /// <summary>送らない。</summary>
    Skip,
}

/// <summary>
/// 転送の依頼 1 件。
/// </summary>
/// <param name="Direction">向き。</param>
/// <param name="LocalPath">パソコン側のパス。</param>
/// <param name="RemotePath">サーバー側のパス。</param>
/// <param name="OnConflict">転送先に同じ名前のファイルがあるときの扱い。</param>
/// <param name="ExpectedBytes">わかっていればファイルの大きさ（始まる前から残り時間を出すため）。</param>
/// <param name="IsFolder">ファイルではなく、空のフォルダを作る依頼か（中身のあるフォルダは、ファイルを送るときに作られる）。</param>
public sealed record TransferRequest(
    TransferDirection Direction,
    string LocalPath,
    string RemotePath,
    ConflictPolicy OnConflict = ConflictPolicy.Overwrite,
    long? ExpectedBytes = null,
    bool IsFolder = false);

/// <summary>
/// 送信待ちの 1 件の、ある時点の様子。画面に出すために使う。
/// </summary>
/// <param name="SizeVerified">完了したとき、転送後に大きさを照合して一致したか。</param>
/// <param name="FinishedAt">完了・失敗・スキップになった時刻。</param>
public sealed record TransferItemSnapshot(
    Guid Id,
    long Sequence,
    TransferDirection Direction,
    string LocalPath,
    string RemotePath,
    TransferState State,
    int FailedAttempts,
    TransferFailure? LastFailure,
    DateTimeOffset? RetryAt,
    long TotalBytes,
    long TransferredBytes,
    string? Note,
    bool SizeVerified = false,
    DateTimeOffset? FinishedAt = null,
    bool IsFolder = false);

/// <summary>送信待ち全体の件数。</summary>
public sealed record TransferSummary(
    int Total,
    int Queued,
    int Running,
    int WaitingToRetry,
    int Completed,
    int Failed,
    int Skipped,
    int Cancelled = 0)
{
    /// <summary>まだ終わっていない件数。</summary>
    public int Pending => Queued + Running + WaitingToRetry;
}

/// <summary>転送の実行が終わった理由。</summary>
public enum TransferRunOutcome
{
    /// <summary>すべての項目が完了・失敗・スキップのどれかになった。</summary>
    Finished,

    /// <summary>サーバーに接続できないため、一時停止した。項目は待機中のまま残る。</summary>
    Paused,

    /// <summary>中止された。転送中だった項目は待機中に戻る。</summary>
    Cancelled,
}

/// <summary>転送の実行結果。</summary>
public sealed record TransferRunResult(
    TransferRunOutcome Outcome,
    TransferSummary Summary,
    TransferFailure? PauseReason = null);

/// <summary>転送の設定。</summary>
public sealed record TransferOptions
{
    private static readonly RetryPolicy DefaultConnectPolicy = new(
        maxAttempts: 8,
        delays:
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
        ]);

    /// <summary>同時に転送する数の上限。サーバーに断られたら自動で減らす。</summary>
    public int MaxConcurrency { get; init; } = 3;

    /// <summary>転送に失敗したときのやり直し方。</summary>
    public RetryPolicy RetryPolicy { get; init; } = RetryPolicy.Default;

    /// <summary>
    /// 接続できないときのやり直し方。接続できないことは項目の失敗には数えない。
    /// やり直しの上限に達したら、送信待ちを一時停止する。
    /// </summary>
    public RetryPolicy ConnectPolicy { get; init; } = DefaultConnectPolicy;

    /// <summary>転送後に大きさを照合する。</summary>
    public bool VerifySize { get; init; } = true;

    /// <summary>更新日時を転送元に合わせる。</summary>
    public bool PreserveModifiedTime { get; init; } = true;

    /// <summary>ダウンロード中のファイルにつける名前の末尾。完了したら外す。</summary>
    public string PartialDownloadSuffix { get; init; } = ".hakobune-part";
}
