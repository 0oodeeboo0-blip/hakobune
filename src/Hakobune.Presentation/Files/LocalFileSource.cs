using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Files;

/// <summary>パソコンのフォルダの一覧。</summary>
public sealed class LocalFileSource(TimeProvider? timeProvider = null) : IFileSource
{
    private static readonly EnumerationOptions Options = new()
    {
        // エクスプローラーと同じく、隠しファイルとシステムファイルは出さない
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<string> GetStartPathAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <inheritdoc />
    public Task<FileListing> ListAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => List(path, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public string? GetParent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            return null;
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(root ?? ""), StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
    }

    /// <inheritdoc />
    public IReadOnlyList<PathSegment> GetSegments(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            return [];

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        // C:\ は「C:」と出す。/ はそのまま
        var rootLabel = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = new List<PathSegment> { new(rootLabel.Length > 0 ? rootLabel : root, root) };

        var current = root;
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        foreach (var part in full[root.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            segments.Add(new PathSegment(part, current));
        }
        return segments;
    }

    private FileListing List(string path, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        try
        {
            var directory = new DirectoryInfo(path);
            var entries = new List<FileEntry>();
            foreach (var info in directory.EnumerateFileSystemInfos("*", Options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(ToEntry(info));
            }
            return new FileListing(directory.FullName, entries, null, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FileListing(path, [], FailureClassifier.FromException(ex), now);
        }
    }

    private static FileEntry ToEntry(FileSystemInfo info)
    {
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return info is FileInfo file
            ? new FileEntry(file.Name, file.FullName, FileEntryType.File, file.Length, modified)
            : new FileEntry(info.Name, info.FullName, FileEntryType.Directory, 0, modified);
    }
}
