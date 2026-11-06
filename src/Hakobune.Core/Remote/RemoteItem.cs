namespace Hakobune.Core.Remote;

/// <summary>サーバー上の項目の種類。</summary>
public enum RemoteItemType
{
    File,
    Directory,
    Link,
}

/// <summary>
/// サーバー上のファイルやフォルダ 1 つ分の情報。
/// </summary>
/// <param name="Name">名前。</param>
/// <param name="FullPath">サーバー上の絶対パス。</param>
/// <param name="Type">種類。</param>
/// <param name="Size">大きさ（バイト）。分からないときは -1。</param>
/// <param name="Modified">更新日時。分からないときは null。</param>
/// <param name="Permissions">属性（例: rwxr-xr-x）。分からないときは null。</param>
public sealed record RemoteItem(
    string Name,
    string FullPath,
    RemoteItemType Type,
    long Size,
    DateTimeOffset? Modified,
    string? Permissions = null)
{
    /// <summary>フォルダかどうか。</summary>
    public bool IsDirectory => Type == RemoteItemType.Directory;
}

/// <summary>
/// 接続先のサーバーが対応している操作。
/// </summary>
/// <param name="CanResumeUpload">アップロードを途中から再開できる。</param>
/// <param name="CanResumeDownload">ダウンロードを途中から再開できる。</param>
/// <param name="CanSetModifiedTime">ファイルの更新日時を設定できる。</param>
public sealed record RemoteCapabilities(bool CanResumeUpload, bool CanResumeDownload, bool CanSetModifiedTime)
{
    /// <summary>どれにも対応していない。</summary>
    public static RemoteCapabilities None { get; } = new(false, false, false);
}
