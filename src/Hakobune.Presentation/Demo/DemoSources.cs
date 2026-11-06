using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;
using Hakobune.Presentation.Transfers;

namespace Hakobune.Presentation.Demo;

/// <summary>時刻が止まった時計（見本の画面写真を毎回同じにするため）。</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}

/// <summary>見本のログを書くときの時計（書く前に時刻を決める）。</summary>
internal sealed class SettableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}

/// <summary>決まった中身を返す、見本の一覧。</summary>
internal sealed class DemoFileSource : IFileSource
{
    private readonly Dictionary<string, IReadOnlyList<FileEntry>> _folders = new(StringComparer.Ordinal);
    private readonly string _startPath;
    private readonly bool _windowsPaths;
    private readonly string? _rootLabel;
    private readonly TimeProvider _time;

    public DemoFileSource(string startPath, bool windowsPaths, TimeProvider time, string? rootLabel = null)
    {
        _startPath = startPath;
        _windowsPaths = windowsPaths;
        _time = time;
        _rootLabel = rootLabel;
    }

    /// <summary>これ以降の一覧の取得を、この理由で失敗させる。</summary>
    public TransferFailure? Failure { get; set; }

    public char Separator => _windowsPaths ? '\\' : '/';

    public void AddFolder(string path, IReadOnlyList<FileEntry> entries) => _folders[path] = entries;

    public string Combine(string folder, string name) =>
        folder.EndsWith(Separator) ? folder + name : folder + Separator + name;

    public Task<string> GetStartPathAsync(CancellationToken cancellationToken) => Task.FromResult(_startPath);

    public Task<FileListing> ListAsync(string path, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (Failure is { } failure)
            return Task.FromResult(new FileListing(path, [], failure, now));
        return Task.FromResult(new FileListing(path, _folders.GetValueOrDefault(path) ?? [], null, now));
    }

    public string? GetParent(string path)
    {
        var trimmed = path.TrimEnd(Separator);
        var index = trimmed.LastIndexOf(Separator);
        if (_windowsPaths)
        {
            // C:\ がいちばん上
            if (index < 0 || trimmed.Length <= 2)
                return null;
            return index == 2 ? trimmed[..3] : trimmed[..index];
        }
        if (trimmed.Length == 0)
            return null;
        return index <= 0 ? "/" : trimmed[..index];
    }

    public IReadOnlyList<PathSegment> GetSegments(string path)
    {
        var segments = new List<PathSegment>();
        var parts = path.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        if (_windowsPaths)
        {
            if (parts.Length == 0)
                return segments;
            var current = parts[0] + Separator;
            segments.Add(new PathSegment(parts[0], current));
            foreach (var part in parts.Skip(1))
            {
                current = Combine(current, part);
                segments.Add(new PathSegment(part, current));
            }
            return segments;
        }

        segments.Add(new PathSegment(_rootLabel ?? "/", "/"));
        var remote = "";
        foreach (var part in parts)
        {
            remote += "/" + part;
            segments.Add(new PathSegment(part, remote));
        }
        return segments;
    }
}

/// <summary>決まった項目を返す、見本の送信待ち。</summary>
internal sealed class DemoTransferSource(IReadOnlyList<TransferItemSnapshot> items, int maxAttempts, int maxConcurrency) : ITransferSource
{
    public event EventHandler<TransferItemSnapshot>? ItemChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<IReadOnlyList<Guid>>? ItemsRemoved
    {
        add { }
        remove { }
    }

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public int MaxAttempts => maxAttempts;

    public int MaxConcurrency => maxConcurrency;

    public bool IsPaused => false;

    public TransferFailure? StopReason => null;

    public int RestoredPendingCount => 0;

    public IReadOnlyList<TransferItemSnapshot> GetItems() => items;

    // 見本の画面では、押しても何も変えない
    public void Pause()
    {
    }

    public void Resume()
    {
    }

    public int RetryFailed() => 0;

    public int Retry(IEnumerable<Guid> ids) => 0;

    public int RetryNow(IEnumerable<Guid> ids) => 0;

    public int Cancel(IEnumerable<Guid> ids) => 0;

    public int RemoveFinished() => 0;
}
