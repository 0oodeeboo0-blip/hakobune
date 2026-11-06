using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Backups;

/// <summary>1 つの接続先の転送で、上書き・削除の前に控えを <see cref="BackupStore"/> に取る。</summary>
public sealed class ConnectionBackups : ITransferBackups
{
    /// <summary><paramref name="store"/> に、<paramref name="connectionId"/> の接続先の控えとして取る。</summary>
    public ConnectionBackups(BackupStore store, Guid connectionId)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        ConnectionId = connectionId;
    }

    /// <summary>控えの置き場。</summary>
    public BackupStore Store { get; }

    /// <summary>接続先の ID。</summary>
    public Guid ConnectionId { get; }

    /// <inheritdoc />
    public async Task<string?> BeforeOverwriteOnServerAsync(
        IRemoteSession session, TransferRequest request, RemoteItem existing, long replacementSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(existing);
        var result = await Store.SaveAsync(
            ConnectionId,
            request.Batch,
            BackupKind.ServerOverwritten,
            request.RemotePath,
            existing.Size,
            existing.Modified,
            replacementSize,
            (stream, token) => session.DownloadAsync(request.RemotePath, stream, 0, null, token),
            cancellationToken).ConfigureAwait(false);
        return result.Note;
    }

    /// <inheritdoc />
    public async Task<string?> BeforeDeleteOnServerAsync(
        IRemoteSession session, TransferRequest request, RemoteItem? existing, bool isDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (isDirectory)
        {
            return await Store.SaveServerFolderAsync(
                ConnectionId,
                request.Batch,
                request.RemotePath,
                (path, token) => session.ListAsync(path, token),
                (path, stream, token) => session.DownloadAsync(path, stream, 0, null, token),
                cancellationToken).ConfigureAwait(false);
        }
        var result = await Store.SaveAsync(
            ConnectionId,
            request.Batch,
            BackupKind.ServerDeleted,
            request.RemotePath,
            existing?.Size ?? -1,
            existing?.Modified,
            null,
            (stream, token) => session.DownloadAsync(request.RemotePath, stream, 0, null, token),
            cancellationToken).ConfigureAwait(false);
        return result.Note;
    }

    /// <inheritdoc />
    public async Task<string?> BeforeOverwriteOnComputerAsync(TransferRequest request, string localPath, long replacementSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Store.SaveLocalAsync(ConnectionId, request.Batch, localPath, replacementSize, cancellationToken).ConfigureAwait(false);
        return result.Note;
    }
}
