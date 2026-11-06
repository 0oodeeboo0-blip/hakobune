using Hakobune.Core.Browsing;
using Hakobune.Core.Remote;

namespace Hakobune.Presentation.Files;

/// <summary>
/// サーバーのフォルダの一覧。画面用の接続（<see cref="RemoteBrowser"/>）だけを使い、転送用の接続には触らない。
/// </summary>
public sealed class RemoteFileSource : IFileSource
{
    private readonly RemoteBrowser _browser;
    private readonly string _rootLabel;

    /// <summary>一覧を <paramref name="browser"/> で取る。パンくずの先頭には <paramref name="rootLabel"/>（ホスト名など）を出す。</summary>
    public RemoteFileSource(RemoteBrowser browser, string rootLabel)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentException.ThrowIfNullOrEmpty(rootLabel);
        _browser = browser;
        _rootLabel = rootLabel;
    }

    /// <inheritdoc />
    public Task<string> GetStartPathAsync(CancellationToken cancellationToken) => _browser.GetHomeDirectoryAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<FileListing> ListAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _browser.ListAsync(path, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FileEntry> entries = result.Succeeded ? result.Items.Select(FileEntry.FromRemote).ToList() : [];
        return new FileListing(result.Path, entries, result.Failure, result.RetrievedAt);
    }

    /// <inheritdoc />
    public string? GetParent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? null : RemotePath.GetParent(trimmed);
    }

    /// <inheritdoc />
    public IReadOnlyList<PathSegment> GetSegments(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = new List<PathSegment> { new(_rootLabel, "/") };
        var current = "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            segments.Add(new PathSegment(part, current));
        }
        return segments;
    }
}
