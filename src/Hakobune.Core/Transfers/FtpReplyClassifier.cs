namespace Hakobune.Core.Transfers;

/// <summary>
/// FTP の否定応答（4xx / 5xx）を、やり直すべきかどうかで分類する。
/// </summary>
public static class FtpReplyClassifier
{
    // 同時接続数の上限に達したときの文言。サーバーによって 421 だったり 530 だったりする。
    // 例: Pure-FTPd「421 Too many connections」、vsftpd「421 There are too many connected users」、
    //     ProFTPD「530 Sorry, the maximum number of clients (3) for this user are already connected.」
    private static readonly string[] TooManyConnectionsHints =
    [
        "too many",
        "maximum number of",
    ];

    // 550 は「権限がない」と「見つからない」の両方に使われるので、文言で見分ける。
    private static readonly string[] NotFoundHints =
    [
        "no such file",
        "not found",
        "does not exist",
        "cannot find",
        "can't find",
    ];

    /// <summary>
    /// 応答コードとサーバーの文言から、失敗を分類する。
    /// </summary>
    /// <param name="replyCode">400〜599 の応答コード。</param>
    /// <param name="serverMessage">サーバーが返した文言。</param>
    /// <exception cref="ArgumentOutOfRangeException">応答コードが 400〜599 の範囲外のとき。</exception>
    public static TransferFailure Classify(int replyCode, string? serverMessage = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replyCode, 400);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(replyCode, 599);

        var reason = GetReason(replyCode, serverMessage);
        var kind = reason switch
        {
            FailureReason.ConnectionLost
                or FailureReason.Timeout
                or FailureReason.DataConnectionFailed
                or FailureReason.ServerBusy
                or FailureReason.TooManyConnections => FailureKind.Transient,

            // RFC 959 では、4xx は一時的な否定応答、5xx は恒久的な否定応答。
            FailureReason.Unknown => replyCode < 500 ? FailureKind.Transient : FailureKind.Permanent,

            _ => FailureKind.Permanent,
        };
        return new TransferFailure(reason, kind, replyCode, serverMessage);
    }

    private static FailureReason GetReason(int replyCode, string? serverMessage)
    {
        if ((replyCode is 421 or 530) && ContainsAny(serverMessage, TooManyConnectionsHints))
            return FailureReason.TooManyConnections;

        return replyCode switch
        {
            421 => FailureReason.ConnectionLost,
            425 => FailureReason.DataConnectionFailed,
            426 => FailureReason.ConnectionLost,
            450 or 451 => FailureReason.ServerBusy,
            452 or 552 => FailureReason.InsufficientStorage,
            530 => FailureReason.AuthenticationFailed,
            550 when ContainsAny(serverMessage, NotFoundHints) => FailureReason.NotFound,
            550 => FailureReason.PermissionDenied,
            553 => FailureReason.CannotCreateFile,
            _ => FailureReason.Unknown,
        };
    }

    private static bool ContainsAny(string? text, string[] hints)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var hint in hints)
        {
            if (text.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
