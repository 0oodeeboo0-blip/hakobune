namespace Hakobune.Core.Transfers;

/// <summary>
/// 失敗を「やり直せば成功する見込みがあるか」で分けたもの。
/// </summary>
public enum FailureKind
{
    /// <summary>回線の瞬断やサーバーの一時的な混雑など。間隔をあけてやり直す。</summary>
    Transient,

    /// <summary>権限不足や容量不足など。やり直しても変わらないので、理由を示して失敗にする。</summary>
    Permanent,
}

/// <summary>
/// 失敗の理由。画面に出す説明もこの値で切り替える。
/// </summary>
public enum FailureReason
{
    /// <summary>接続が切れた、またはサーバーが接続を閉じた。</summary>
    ConnectionLost,

    /// <summary>応答が一定時間返ってこなかった。</summary>
    Timeout,

    /// <summary>データ用の接続を開けなかった。</summary>
    DataConnectionFailed,

    /// <summary>サーバー側の一時的なエラー、またはファイルが使用中。</summary>
    ServerBusy,

    /// <summary>サーバーの同時接続数の上限に達した。</summary>
    TooManyConnections,

    /// <summary>権限がない。</summary>
    PermissionDenied,

    /// <summary>ファイルやフォルダが見つからない。</summary>
    NotFound,

    /// <summary>サーバーの容量が足りない。</summary>
    InsufficientStorage,

    /// <summary>ファイルを作れない。名前が使えない場合と権限がない場合があり、サーバーによって意味が違う。</summary>
    CannotCreateFile,

    /// <summary>同じ名前のフォルダがあるため、ファイルを置けない。</summary>
    NameConflictsWithDirectory,

    /// <summary>ログインできない。</summary>
    AuthenticationFailed,

    /// <summary>送った（受け取った）あとの照合で、大きさが合わなかった。</summary>
    VerificationFailed,

    /// <summary>パソコン側のファイルが見つからない。</summary>
    LocalFileNotFound,

    /// <summary>パソコン側のファイルやフォルダにアクセスできない。</summary>
    LocalAccessDenied,

    /// <summary>パソコン側の読み書きで一時的なエラーが起きた（ほかのアプリが使用中など）。</summary>
    LocalIoError,

    /// <summary>上のどれにも当てはまらない。</summary>
    Unknown,
}

/// <summary>
/// 1 回の転送の失敗。
/// </summary>
/// <param name="Reason">失敗の理由。</param>
/// <param name="Kind">やり直す価値があるか。</param>
/// <param name="ReplyCode">サーバーの応答コード（FTP のときのみ）。</param>
/// <param name="ServerMessage">サーバーが返した文言など、詳しい説明（あれば）。</param>
public sealed record TransferFailure(
    FailureReason Reason,
    FailureKind Kind,
    int? ReplyCode = null,
    string? ServerMessage = null)
{
    /// <summary>やり直せば成功する見込みがあるか。</summary>
    public bool IsTransient => Kind == FailureKind.Transient;

    /// <summary>一時的な失敗を作る。</summary>
    public static TransferFailure Transient(FailureReason reason, string? message = null) =>
        new(reason, FailureKind.Transient, null, message);

    /// <summary>やり直しても変わらない失敗を作る。</summary>
    public static TransferFailure Permanent(FailureReason reason, string? message = null) =>
        new(reason, FailureKind.Permanent, null, message);
}
