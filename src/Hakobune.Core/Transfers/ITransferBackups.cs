using Hakobune.Core.Remote;

namespace Hakobune.Core.Transfers;

/// <summary>
/// 上書き・削除する前に、元のファイルの控えを取る（<see cref="TransferOptions.Backups"/>）。
/// 戻り値は、送信待ちの項目に出す一言（「控えあり」など。取らない設定なら null）。
/// 控えを取れなかった（受け取りに失敗した）ときは例外で知らせ、その項目はふつうの失敗と同じくやり直す。
/// </summary>
public interface ITransferBackups
{
    /// <summary>サーバーのファイルを上書きする前に。<paramref name="replacementSize"/> は送る新しいファイルの大きさ。</summary>
    Task<string?> BeforeOverwriteOnServerAsync(
        IRemoteSession session, TransferRequest request, RemoteItem existing, long replacementSize, CancellationToken cancellationToken);

    /// <summary>サーバーのファイルかフォルダを消す前に。フォルダなら中身ごと。</summary>
    Task<string?> BeforeDeleteOnServerAsync(
        IRemoteSession session, TransferRequest request, RemoteItem? existing, bool isDirectory, CancellationToken cancellationToken);

    /// <summary>パソコンのファイルを上書きする前に（受け取ったとき）。</summary>
    Task<string?> BeforeOverwriteOnComputerAsync(TransferRequest request, string localPath, long replacementSize, CancellationToken cancellationToken);
}
