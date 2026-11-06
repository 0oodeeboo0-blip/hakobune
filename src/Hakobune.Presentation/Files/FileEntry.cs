using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Files;

/// <summary>一覧の項目の種類。</summary>
public enum FileEntryType
{
    /// <summary>ファイル。</summary>
    File,

    /// <summary>フォルダ。</summary>
    Directory,

    /// <summary>シンボリックリンク。</summary>
    Link,
}

/// <summary>パソコン側・サーバー側に共通の、一覧の 1 項目。</summary>
/// <param name="Name">名前。</param>
/// <param name="FullPath">フルパス。</param>
/// <param name="Type">種類。</param>
/// <param name="Size">大きさ（バイト）。フォルダでは使わない。</param>
/// <param name="Modified">更新日時。</param>
/// <param name="Permissions">属性（例: -rw-r--r--）。サーバー側だけ。</param>
public sealed record FileEntry(
    string Name,
    string FullPath,
    FileEntryType Type,
    long Size,
    DateTimeOffset? Modified,
    string? Permissions = null)
{
    /// <summary>フォルダか。</summary>
    public bool IsDirectory => Type == FileEntryType.Directory;

    /// <summary>サーバーの一覧の項目から作る。</summary>
    public static FileEntry FromRemote(RemoteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var type = item.Type switch
        {
            RemoteItemType.Directory => FileEntryType.Directory,
            RemoteItemType.Link => FileEntryType.Link,
            _ => FileEntryType.File,
        };
        return new FileEntry(item.Name, item.FullPath, type, item.Size, item.Modified, item.Permissions);
    }
}

/// <summary>パスの区切りの 1 つ（パンくずの 1 項目）。</summary>
/// <param name="Label">表示する名前。</param>
/// <param name="Path">そこまでのパス。</param>
public sealed record PathSegment(string Label, string Path)
{
    /// <summary>パンくずにそのまま表示できるよう、名前を返す。</summary>
    public override string ToString() => Label;
}

/// <summary>フォルダの一覧を取った結果。</summary>
/// <param name="Path">一覧を取ったフォルダ（正規化したもの）。</param>
/// <param name="Entries">項目。失敗したときは空。</param>
/// <param name="Failure">失敗したときの理由。</param>
/// <param name="RetrievedAt">取った時刻。</param>
public sealed record FileListing(
    string Path,
    IReadOnlyList<FileEntry> Entries,
    TransferFailure? Failure,
    DateTimeOffset RetrievedAt)
{
    /// <summary>取れたか。</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// 一覧を出す相手（パソコンのフォルダ、またはサーバー）。画面はこれを通してだけ一覧を取る。
/// </summary>
public interface IFileSource
{
    /// <summary>最初に開くフォルダ。</summary>
    Task<string> GetStartPathAsync(CancellationToken cancellationToken);

    /// <summary>
    /// フォルダの一覧を取る。失敗は例外ではなく <see cref="FileListing.Failure"/> で返す。
    /// </summary>
    Task<FileListing> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>1 つ上のフォルダ。いちばん上なら null。</summary>
    string? GetParent(string path);

    /// <summary>パンくずに出す区切り。先頭がいちばん上のフォルダ。</summary>
    IReadOnlyList<PathSegment> GetSegments(string path);
}
