using System.Globalization;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Text;

/// <summary>
/// 画面やログに出す日本語の文言。画面から切り離しておき、テストで確かめられるようにする。
/// </summary>
public static class DisplayText
{
    private static readonly string[] LargeUnits = ["KB", "MB", "GB", "TB", "PB"];

    /// <summary>失敗の理由を、利用者向けの短い文にする。FTP の応答コードがあれば括弧で添える。</summary>
    public static string Describe(TransferFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var text = failure.Reason switch
        {
            FailureReason.ConnectionLost => "接続が切れました",
            FailureReason.Timeout => "サーバーの応答がありません",
            FailureReason.DataConnectionFailed => "データ用の接続を開けませんでした",
            FailureReason.ServerBusy => "サーバーが一時的に使えません",
            FailureReason.TooManyConnections => "サーバーの同時接続数の上限です",
            FailureReason.PermissionDenied => "権限がありません",
            FailureReason.NotFound => "見つかりません",
            FailureReason.InsufficientStorage => "サーバーの容量が足りません",
            FailureReason.CannotCreateFile => "ファイルを作れません（名前か権限を確かめてください）",
            FailureReason.NameConflictsWithDirectory => "同じ名前のフォルダがあります",
            FailureReason.NameConflictsWithFile => "同じ名前のファイルがあるので、フォルダを作れません",
            FailureReason.AuthenticationFailed => "ログインできません（ユーザー名かパスワードを確かめてください）",
            FailureReason.KeyPassphraseRequired => "秘密鍵のパスフレーズが違うか、入っていません",
            FailureReason.KeyFileUnreadable => "秘密鍵のファイルを読めません（形式を確かめてください）",
            FailureReason.CertificateRejected => "サーバーの証明書（鍵）を信頼できません",
            FailureReason.SecureConnectionFailed => "暗号化した接続を始められません",
            FailureReason.VerificationFailed => "転送後の照合で大きさが合いません",
            FailureReason.LocalFileNotFound => "パソコン側のファイルが見つかりません",
            FailureReason.LocalAccessDenied => "パソコン側のファイルにアクセスできません",
            FailureReason.LocalIoError => "パソコン側のファイルを読み書きできません",
            _ => "予期しないエラーが起きました",
        };
        return failure.ReplyCode is { } code ? $"{text}（{code.ToString(CultureInfo.InvariantCulture)}）" : text;
    }

    /// <summary>やり直しを待っている理由（「〜ため」の形）。</summary>
    public static string RetryReason(TransferFailure? failure) => failure?.Reason switch
    {
        FailureReason.ConnectionLost => "接続が切れたため",
        FailureReason.Timeout => "応答がないため",
        FailureReason.DataConnectionFailed => "データ用の接続を開けなかったため",
        FailureReason.ServerBusy => "サーバーが混んでいるため",
        FailureReason.TooManyConnections => "同時接続数の上限のため",
        FailureReason.VerificationFailed => "大きさが合わなかったため",
        FailureReason.LocalIoError => "ファイルを読み書きできなかったため",
        _ => "失敗したため",
    };

    /// <summary>送信待ちの状態の短い名前。</summary>
    public static string StateLabel(TransferState state) => state switch
    {
        TransferState.Queued => "待機中",
        TransferState.Running => "転送中",
        TransferState.WaitingToRetry => "再試行待ち",
        TransferState.Completed => "完了",
        TransferState.Failed => "失敗",
        TransferState.Skipped => "スキップ",
        TransferState.Cancelled => "取り消し",
        _ => "",
    };

    /// <summary>
    /// データの大きさ（例: 512 バイト、9.5 KB、46 KB、6.3 MB、128.4 MB）。1024 で区切り、1000 に達したら次の単位にする。
    /// </summary>
    public static string Size(long bytes)
    {
        if (bytes < 0)
            return "";
        if (bytes < 1024)
            return $"{bytes.ToString(CultureInfo.InvariantCulture)} バイト";

        var value = bytes / 1024d;
        var unit = 0;
        while (value >= 999.95 && unit < LargeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 && value >= 9.95 ? "0" : "0.0";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {LargeUnits[unit]}";
    }

    /// <summary>
    /// ファイルの一覧に出す大きさ。エクスプローラーと同じく、1 MB 未満は KB に切り上げる（例: 1 KB、18 KB）。
    /// </summary>
    public static string FileSize(long bytes)
    {
        if (bytes < 0)
            return "";
        if (bytes == 0)
            return "0 KB";
        if (bytes < 1024 * 1024)
        {
            var kilobytes = (bytes + 1023) / 1024;
            return $"{kilobytes.ToString("N0", CultureInfo.InvariantCulture)} KB";
        }
        return Size(bytes);
    }

    /// <summary>転送の速さ（例: 6.3 MB/s）。</summary>
    public static string Speed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0)
            return "";
        var bytes = (long)Math.Round(bytesPerSecond);
        return bytes < 1024
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B/s"
            : Size(bytes) + "/s";
    }

    /// <summary>残り時間のおおよそ（例: 残り 2秒、残り 約45秒、残り 約4分、残り 約1時間20分）。</summary>
    public static string Remaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "";

        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds <= 10)
            return $"残り {seconds}秒";
        var roundedSeconds = (seconds + 4) / 5 * 5;
        if (roundedSeconds < 60)
            return $"残り 約{roundedSeconds}秒";

        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        if (minutes < 60)
            return $"残り 約{minutes}分";

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"残り 約{hours}時間" : $"残り 約{hours}時間{rest}分";
    }
}
