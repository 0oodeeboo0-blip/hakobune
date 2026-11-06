using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Transfers;

public class FtpReplyClassifierTests
{
    [Theory]
    // 同時接続数の上限（サーバーによって 421 と 530 がある）
    [InlineData(421, "Too many connections (10) from this IP", FailureReason.TooManyConnections, FailureKind.Transient)]
    [InlineData(421, "There are too many connected users, please try later.", FailureReason.TooManyConnections, FailureKind.Transient)]
    [InlineData(530, "Sorry, the maximum number of clients (3) for this user are already connected.", FailureReason.TooManyConnections, FailureKind.Transient)]
    // 一時的な失敗
    [InlineData(421, "Timeout.", FailureReason.ConnectionLost, FailureKind.Transient)]
    [InlineData(425, "Can't open data connection.", FailureReason.DataConnectionFailed, FailureKind.Transient)]
    [InlineData(426, "Connection closed; transfer aborted.", FailureReason.ConnectionLost, FailureKind.Transient)]
    [InlineData(450, "File busy.", FailureReason.ServerBusy, FailureKind.Transient)]
    [InlineData(451, "Local error in processing.", FailureReason.ServerBusy, FailureKind.Transient)]
    // やり直しても変わらない失敗
    [InlineData(452, "Insufficient storage space.", FailureReason.InsufficientStorage, FailureKind.Permanent)]
    [InlineData(552, "Exceeded storage allocation.", FailureReason.InsufficientStorage, FailureKind.Permanent)]
    [InlineData(530, "Login incorrect.", FailureReason.AuthenticationFailed, FailureKind.Permanent)]
    // 暗号化（FTPS）でないと入れないサーバー
    [InlineData(530, "Non-anonymous sessions must use encryption.", FailureReason.EncryptionRequired, FailureKind.Permanent)]
    [InlineData(534, "Policy requires SSL.", FailureReason.EncryptionRequired, FailureKind.Permanent)]
    [InlineData(550, "SSL/TLS required on the control channel", FailureReason.EncryptionRequired, FailureKind.Permanent)]
    [InlineData(530, "This server does not allow plain FTP. You have to use FTP over TLS.", FailureReason.EncryptionRequired, FailureKind.Permanent)]
    [InlineData(421, "Sorry, cleartext sessions and weak ciphers are not accepted on this server.", FailureReason.EncryptionRequired, FailureKind.Permanent)]
    // ファイル名に ssl が入っているだけなら、ふつうの権限の失敗
    [InlineData(550, "/var/www/ssl: Permission denied", FailureReason.PermissionDenied, FailureKind.Permanent)]
    [InlineData(550, "Permission denied.", FailureReason.PermissionDenied, FailureKind.Permanent)]
    [InlineData(550, "/var/www/html/a.txt: No such file or directory", FailureReason.NotFound, FailureKind.Permanent)]
    [InlineData(553, "Could not create file.", FailureReason.CannotCreateFile, FailureKind.Permanent)]
    // 知らないコードは RFC 959 に従う（4xx は一時的、5xx は恒久的）
    [InlineData(499, null, FailureReason.Unknown, FailureKind.Transient)]
    [InlineData(599, null, FailureReason.Unknown, FailureKind.Permanent)]
    public void Classify_returns_reason_and_kind(int code, string? message, FailureReason reason, FailureKind kind)
    {
        var failure = FtpReplyClassifier.Classify(code, message);

        Assert.Equal(reason, failure.Reason);
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(code, failure.ReplyCode);
        Assert.Equal(message, failure.ServerMessage);
    }

    [Theory]
    [InlineData(226)]
    [InlineData(399)]
    [InlineData(600)]
    public void Classify_rejects_codes_that_are_not_failures(int code)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FtpReplyClassifier.Classify(code));
    }

    [Fact]
    public void Classify_matches_hints_case_insensitively()
    {
        var failure = FtpReplyClassifier.Classify(421, "TOO MANY USERS");

        Assert.Equal(FailureReason.TooManyConnections, failure.Reason);
    }
}
