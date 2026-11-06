using System.Net.Sockets;
using Hakobune.Core.Remote;

namespace Hakobune.Core.Transfers;

/// <summary>
/// 例外を、転送の失敗に置き換える。
/// </summary>
public static class FailureClassifier
{
    /// <summary>例外から失敗の理由と種類を決める。</summary>
    public static TransferFailure FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            RemoteOperationException remote => remote.Failure,

            // FileNotFoundException などは IOException の一種なので、IOException より先に調べる。
            FileNotFoundException or DirectoryNotFoundException =>
                TransferFailure.Permanent(FailureReason.LocalFileNotFound, exception.Message),
            UnauthorizedAccessException =>
                TransferFailure.Permanent(FailureReason.LocalAccessDenied, exception.Message),

            // 通信の失敗が IOException に包まれて届くこともある。
            SocketException or IOException { InnerException: SocketException } =>
                TransferFailure.Transient(FailureReason.ConnectionLost, exception.Message),
            TimeoutException or OperationCanceledException =>
                TransferFailure.Transient(FailureReason.Timeout, exception.Message),
            IOException =>
                TransferFailure.Transient(FailureReason.LocalIoError, exception.Message),

            _ => TransferFailure.Permanent(FailureReason.Unknown, exception.Message),
        };
    }
}
