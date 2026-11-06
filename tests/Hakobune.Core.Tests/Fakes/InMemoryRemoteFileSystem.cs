using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Fakes;

/// <summary>テスト用の、メモリ上のサーバーのファイル置き場。</summary>
internal sealed class InMemoryRemoteFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Data, DateTimeOffset Modified)> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "/" };

    public int FileCount
    {
        get
        {
            lock (_gate)
                return _files.Count;
        }
    }

    public void CreateDirectory(string path)
    {
        lock (_gate)
        {
            for (var current = Normalize(path); current.Length > 0 && current != "/"; current = RemotePath.GetParent(current))
            {
                if (_files.ContainsKey(current))
                    throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.CannotCreateFile, current));
                _directories.Add(current);
            }
        }
    }

    public void WriteFile(string path, ReadOnlySpan<byte> data, long offset, DateTimeOffset modified)
    {
        lock (_gate)
        {
            var normalized = Normalize(path);
            var parent = RemotePath.GetParent(normalized);
            if (!_directories.Contains(parent))
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound, $"{parent} がありません"));
            if (_directories.Contains(normalized))
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NameConflictsWithDirectory, normalized));

            var existing = _files.TryGetValue(normalized, out var entry) ? entry.Data : Array.Empty<byte>();
            if (offset > existing.Length)
                throw new InvalidOperationException($"書き込み位置 {offset} が、いまの大きさ {existing.Length} を超えています。");

            var result = new byte[offset + data.Length];
            existing.AsSpan(0, (int)offset).CopyTo(result);
            data.CopyTo(result.AsSpan((int)offset));
            _files[normalized] = (result, modified);
        }
    }

    public byte[]? ReadFile(string path)
    {
        lock (_gate)
            return _files.TryGetValue(Normalize(path), out var entry) ? entry.Data.ToArray() : null;
    }

    public RemoteItem? GetItem(string path)
    {
        lock (_gate)
        {
            var normalized = Normalize(path);
            if (_files.TryGetValue(normalized, out var entry))
                return new RemoteItem(RemotePath.GetFileName(normalized), normalized, RemoteItemType.File, entry.Data.Length, entry.Modified);
            if (_directories.Contains(normalized))
                return new RemoteItem(RemotePath.GetFileName(normalized), normalized, RemoteItemType.Directory, 0, null);
            return null;
        }
    }

    public IReadOnlyList<RemoteItem> List(string path)
    {
        lock (_gate)
        {
            var directory = Normalize(path);
            if (!_directories.Contains(directory))
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound, directory));

            var items = new List<RemoteItem>();
            foreach (var child in _directories)
            {
                if (child != "/" && RemotePath.GetParent(child) == directory)
                    items.Add(new RemoteItem(RemotePath.GetFileName(child), child, RemoteItemType.Directory, 0, null));
            }
            foreach (var (filePath, entry) in _files)
            {
                if (RemotePath.GetParent(filePath) == directory)
                    items.Add(new RemoteItem(RemotePath.GetFileName(filePath), filePath, RemoteItemType.File, entry.Data.Length, entry.Modified));
            }
            return items;
        }
    }

    public void SetModified(string path, DateTimeOffset modified)
    {
        lock (_gate)
        {
            var normalized = Normalize(path);
            if (!_files.TryGetValue(normalized, out var entry))
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound, normalized));
            _files[normalized] = (entry.Data, modified);
        }
    }

    public void Delete(string path)
    {
        lock (_gate)
        {
            if (!_files.Remove(Normalize(path)))
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound, path));
        }
    }

    public void Rename(string fromPath, string toPath)
    {
        lock (_gate)
        {
            var from = Normalize(fromPath);
            if (!_files.Remove(from, out var entry))
                throw new RemoteOperationException(TransferFailure.Permanent(FailureReason.NotFound, fromPath));
            _files[Normalize(toPath)] = entry;
        }
    }

    private static string Normalize(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}
