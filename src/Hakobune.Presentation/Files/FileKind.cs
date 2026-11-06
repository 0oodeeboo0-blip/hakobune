namespace Hakobune.Presentation.Files;

/// <summary>アイコンを選ぶための、ファイルの大まかな種類。</summary>
public enum FileKind
{
    /// <summary>フォルダ。</summary>
    Folder,

    /// <summary>ドライブ。</summary>
    Drive,

    /// <summary>HTML やプログラム。</summary>
    Code,

    /// <summary>画像。</summary>
    Image,

    /// <summary>圧縮ファイル。</summary>
    Archive,

    /// <summary>テキストや設定ファイル。</summary>
    Text,

    /// <summary>PDF や Office の文書。</summary>
    Document,

    /// <summary>音声・動画。</summary>
    Media,

    /// <summary>そのほか。</summary>
    Other,
}

/// <summary>名前（拡張子）からファイルの種類を決める。</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    /// <summary>項目の種類を返す。</summary>
    public static FileKind Of(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Type switch
        {
            FileEntryType.Drive => FileKind.Drive,
            FileEntryType.Directory => FileKind.Folder,
            _ => FromName(entry.Name),
        };
    }

    /// <summary>ファイル名から種類を返す。</summary>
    public static FileKind FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
            return FileKind.Other;
        return ByExtension.TryGetValue(name[dot..], out var kind) ? kind : FileKind.Other;
    }

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, params string[] extensions)
        {
            foreach (var extension in extensions)
                map[extension] = kind;
        }

        Add(FileKind.Code,
            ".html", ".htm", ".xhtml", ".php", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".sass", ".less",
            ".json", ".py", ".rb", ".pl", ".cgi", ".sh", ".bat", ".cmd", ".ps1", ".java", ".c", ".cpp", ".h", ".cs", ".go",
            ".rs", ".vue", ".svelte", ".asp", ".aspx", ".jsp", ".sql", ".twig", ".tpl");
        Add(FileKind.Image,
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".tif", ".tiff", ".avif", ".heic");
        Add(FileKind.Archive,
            ".zip", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".7z", ".rar", ".lzh", ".zst");
        Add(FileKind.Text,
            ".txt", ".md", ".log", ".csv", ".tsv", ".xml", ".ini", ".conf", ".cfg", ".yml", ".yaml", ".toml", ".env",
            ".htaccess", ".htpasswd", ".gitignore", ".editorconfig");
        Add(FileKind.Document,
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf");
        Add(FileKind.Media,
            ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg");
        return map;
    }
}
