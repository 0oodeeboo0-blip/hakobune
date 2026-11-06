namespace Hakobune.Core.Remote;

/// <summary>
/// サーバー側のパス（区切りは「/」）を扱う。
/// </summary>
public static class RemotePath
{
    /// <summary>フォルダのパスと名前をつなぐ。</summary>
    public static string Combine(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (directory.Length == 0)
            return name;
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    /// <summary>親フォルダのパスを返す。ルートの親はルート、相対パスの先頭の親は空文字。</summary>
    public static string GetParent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
            return path.StartsWith('/') ? "/" : "";

        var index = trimmed.LastIndexOf('/');
        return index switch
        {
            < 0 => "",
            0 => "/",
            _ => trimmed[..index],
        };
    }

    /// <summary>パスの最後の名前を返す。</summary>
    public static string GetFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }
}
