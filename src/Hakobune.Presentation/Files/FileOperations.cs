namespace Hakobune.Presentation.Files;

/// <summary>パソコンのファイルの操作（Windows では、消すときにごみ箱へ移す）。</summary>
public interface ILocalFileOperations
{
    /// <summary>フォルダを作る。</summary>
    void CreateDirectory(string path);

    /// <summary>ファイルかフォルダの名前を変える。</summary>
    void Rename(string fromPath, string toPath);

    /// <summary>ファイルとフォルダを消す。</summary>
    void Delete(IReadOnlyList<string> paths);

    /// <summary>消したものを、ごみ箱から戻せるか（確認の文に出す）。</summary>
    bool UsesRecycleBin { get; }
}

/// <summary>そのまま消す（ごみ箱を使わない）。試験と、ごみ箱が無いときに使う。</summary>
public sealed class LocalFileOperations : ILocalFileOperations
{
    /// <summary>共有のもの。</summary>
    public static LocalFileOperations Instance { get; } = new();

    /// <inheritdoc />
    public bool UsesRecycleBin => false;

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void Rename(string fromPath, string toPath)
    {
        if (Directory.Exists(fromPath))
            Directory.Move(fromPath, toPath);
        else
            File.Move(fromPath, toPath);
    }

    /// <inheritdoc />
    public void Delete(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
        }
    }
}

/// <summary>ファイルの操作のときに利用者に聞くこと（画面がダイアログで聞く）。</summary>
public interface IFileDialogs
{
    /// <summary>
    /// 名前を聞く（新しいフォルダ、名前の変更）。<paramref name="validate"/> は問題があれば説明を返す。やめたら null。
    /// </summary>
    Task<string?> AskNameAsync(string title, string initialName, string primaryButtonText, Func<string, string?> validate);

    /// <summary>
    /// 消してよいか聞く。<paramref name="recoverable"/> は元に戻せるか（パソコンはごみ箱に移すとき、サーバーは控えを取るとき）。
    /// </summary>
    Task<bool> ConfirmDeleteAsync(IReadOnlyList<string> names, bool remote, bool recoverable);

    /// <summary>属性（パーミッション）を聞く。<paramref name="currentMode"/> がわかればそれを最初に出す。やめたら null。</summary>
    Task<int?> AskPermissionsAsync(IReadOnlyList<string> names, int? currentMode);
}

/// <summary>ファイル名の確かめ方。</summary>
public static class FileNames
{
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// 名前に問題があれば、利用者向けの説明を返す（なければ null）。パソコン側は Windows の決まりで確かめる。
    /// </summary>
    public static string? Validate(string name, PaneKind kind)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Trim().Length == 0)
            return "名前を入れてください";
        if (name is "." or "..")
            return "「.」と「..」は名前に使えません";
        if (name.Contains('/', StringComparison.Ordinal))
            return "「/」は名前に使えません";
        if (kind == PaneKind.Remote)
            return name.Any(char.IsControl) ? "使えない文字が入っています" : null;

        if (name.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
            return "\\ : * ? \" < > | は名前に使えません";
        if (name.EndsWith('.') || name.EndsWith(' '))
            return "名前の終わりに「.」や空白は使えません";
        var stem = name.Split('.')[0].Trim();
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return $"「{stem}」は Windows が使う名前なので使えません";
        return null;
    }
}
