using Hakobune.Core.Transfers;

namespace Hakobune.Core.Remote;

/// <summary>
/// サーバーへの接続 1 本。FTP・FTPS・SFTP の違いはこの裏に隠す。
/// </summary>
/// <remarks>
/// 1 つの接続は、同時に 1 か所からしか使わない（FFFTP のように画面と転送で取り合わない）。
/// 失敗はすべて <see cref="RemoteOperationException"/> で知らせ、
/// 一時的な失敗のあとは <see cref="IsConnected"/> を false にする。
/// </remarks>
public interface IRemoteSession : IAsyncDisposable
{
    /// <summary>接続できていて、まだ使えるか。</summary>
    bool IsConnected { get; }

    /// <summary>サーバーが対応している操作。接続したあとに確定する。</summary>
    RemoteCapabilities Capabilities { get; }

    /// <summary>接続してログインする。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>ログイン直後のフォルダ（ホーム）の絶対パス。</summary>
    Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken);

    /// <summary>フォルダの中身を返す。</summary>
    Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 項目の情報を返す。無ければ null。MLST の無い FTP サーバー（vsftpd など）では、フォルダも null になる
    /// （ファイルを速く調べるため。フォルダかどうかは <see cref="DirectoryExistsAsync"/> で調べる）。
    /// </summary>
    Task<RemoteItem?> GetItemAsync(string path, CancellationToken cancellationToken);

    /// <summary>フォルダがあるか。<see cref="GetItemAsync"/> でフォルダを見分けられないサーバーでも分かる。</summary>
    async Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        await GetItemAsync(path, cancellationToken).ConfigureAwait(false) is { IsDirectory: true };

    /// <summary>フォルダを親フォルダごと作る。すでにあれば何もしない。</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="source"/> の現在位置から最後までを、サーバー上の <paramref name="offset"/> バイト目以降に書き込む。
    /// <paramref name="offset"/> が 0 のときは上書きする。
    /// </summary>
    /// <param name="progress">この呼び出しで送ったバイト数を知らせる。</param>
    Task UploadAsync(Stream source, string remotePath, long offset, IProgress<long>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// サーバー上のファイルの <paramref name="offset"/> バイト目以降を、<paramref name="destination"/> の現在位置に書き込む。
    /// </summary>
    /// <param name="progress">この呼び出しで受け取ったバイト数を知らせる。</param>
    Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken);

    /// <summary>ファイルを削除する。</summary>
    Task DeleteFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>フォルダを中身ごと削除する。</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// 属性（パーミッション）を変える。<paramref name="mode"/> は 8 進の 0644 なら 420（<c>Convert.ToInt32("644", 8)</c>）。
    /// Windows の FTP サーバーなど、対応していないサーバーもある。
    /// </summary>
    Task SetPermissionsAsync(string path, int mode, CancellationToken cancellationToken);

    /// <summary>名前を変える（移動する）。</summary>
    Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken);

    /// <summary>ファイルの更新日時を設定する。</summary>
    Task SetModifiedTimeAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken);

    /// <summary>何もしていない間に接続が切られないよう、軽いコマンドを送る。</summary>
    Task KeepAliveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 接続を作る。転送の仕組みは、必要な数だけここから接続を作る。
/// </summary>
public interface IRemoteSessionFactory
{
    /// <summary>まだ接続していない新しい接続を作る。</summary>
    IRemoteSession Create();
}

/// <summary>
/// サーバーとのやり取りの失敗。理由は <see cref="Failure"/> で分かる。
/// </summary>
public sealed class RemoteOperationException : Exception
{
    public RemoteOperationException(TransferFailure failure, string? message = null, Exception? innerException = null)
        : base(message ?? failure?.ServerMessage ?? failure?.Reason.ToString(), innerException)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Failure = failure;
    }

    /// <summary>失敗の理由と、やり直す価値があるか。</summary>
    public TransferFailure Failure { get; }
}
