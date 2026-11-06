using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Files;

/// <summary>パソコンのフォルダの一覧。Windows では、いちばん上に「PC」（ドライブの一覧）を置く。</summary>
public sealed class LocalFileSource : IFileSource
{
    /// <summary>ドライブの一覧（「PC」）を表すパス。</summary>
    public const string ComputerPath = "";

    private static readonly EnumerationOptions Options = new()
    {
        // エクスプローラーと同じく、隠しファイルとシステムファイルは出さない
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly TimeProvider _time;
    private readonly bool _computerRoot;

    /// <summary>
    /// 一覧を作る。<paramref name="showComputerRoot"/> を省くと、Windows のときだけ「PC」を置く。
    /// </summary>
    public LocalFileSource(TimeProvider? timeProvider = null, bool? showComputerRoot = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _computerRoot = showComputerRoot ?? OperatingSystem.IsWindows();
    }

    /// <inheritdoc />
    public Task<string> GetStartPathAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <inheritdoc />
    public Task<FileListing> ListAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => path.Length == 0 && _computerRoot ? ListDrives() : List(path, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public string? GetParent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            return null;
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        if (string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            return _computerRoot ? ComputerPath : null;
        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
    }

    /// <inheritdoc />
    public IReadOnlyList<PathSegment> GetSegments(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = new List<PathSegment>();
        if (_computerRoot)
            segments.Add(new PathSegment("PC", ComputerPath));
        if (path.Length == 0)
            return segments;

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        // C:\ は「C:」と出す。/ はそのまま
        var rootLabel = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        segments.Add(new PathSegment(rootLabel.Length > 0 ? rootLabel : root, root));

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

    private FileListing ListDrives()
    {
        var entries = new List<FileEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (letter.Length == 0)
                letter = drive.Name;
            entries.Add(new FileEntry($"{DriveLabel(drive)} ({letter})", drive.RootDirectory.FullName, FileEntryType.Drive, 0, null));
        }
        return new FileListing(ComputerPath, entries, null, _time.GetUtcNow());
    }

    // ドライブの名前。名前が付いていなければ、種類で呼ぶ（エクスプローラーと同じ）
    private static string DriveLabel(DriveInfo drive)
    {
        try
        {
            if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
                return drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 準備ができていないドライブ（空の DVD など）は、種類の名前にする
        }
        return drive.DriveType switch
        {
            DriveType.Fixed => "ローカル ディスク",
            DriveType.Removable => "リムーバブル ディスク",
            DriveType.Network => "ネットワーク ドライブ",
            DriveType.CDRom => "DVD ドライブ",
            DriveType.Ram => "RAM ディスク",
            _ => "ドライブ",
        };
    }

    private static FileEntry ToEntry(FileSystemInfo info)
    {
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return info is FileInfo file
            ? new FileEntry(file.Name, file.FullName, FileEntryType.File, file.Length, modified)
            : new FileEntry(info.Name, info.FullName, FileEntryType.Directory, 0, modified);
    }
}
