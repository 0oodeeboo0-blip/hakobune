using System.Collections.Concurrent;
using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Tests.Transfers;

/// <summary>受け取ったファイルをメモリに置くだけの、小さな偽のサーバー（送信待ちを動かす試験用）。</summary>
internal sealed class MemoryTransferServer : IRemoteSessionFactory
{
    private int _connects;

    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>アップロードの最初に待たせたいとき（パス、中止の合図）。</summary>
    public Func<string, CancellationToken, Task>? BeforeUpload { get; set; }

    /// <summary>接続を失敗させたいとき。</summary>
    public TransferFailure? ConnectFault { get; set; }

    public int Connects => Volatile.Read(ref _connects);

    public IRemoteSession Create() => new Session(this);

    private sealed class Session(MemoryTransferServer server) : IRemoteSession
    {
        public bool IsConnected { get; private set; }

        public RemoteCapabilities Capabilities => RemoteCapabilities.None;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (server.ConnectFault is { } fault)
                throw new RemoteOperationException(fault);
            Interlocked.Increment(ref server._connects);
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult("/");

        public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteItem>>([]);

        public Task<RemoteItem?> GetItemAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteItem?>(server.Files.TryGetValue(path, out var data)
                ? new RemoteItem(RemotePath.GetFileName(path), path, RemoteItemType.File, data.Length, DateTimeOffset.UtcNow)
                : null);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task UploadAsync(Stream source, string remotePath, long offset, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (server.BeforeUpload is { } before)
                await before(remotePath, cancellationToken);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            server.Files[remotePath] = buffer.ToArray();
            progress?.Report(buffer.Length);
        }

        public Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetPermissionsAsync(string path, int mode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetModifiedTimeAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task KeepAliveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
