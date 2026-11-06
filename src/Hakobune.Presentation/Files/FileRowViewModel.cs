using System.Globalization;
using Hakobune.Core.Text;

namespace Hakobune.Presentation.Files;

/// <summary>一覧の 1 行。中身は変わらないので、変更の通知はしない。</summary>
public sealed class FileRowViewModel
{
    /// <summary>項目から行を作る。日時は <paramref name="timeZone"/> の時刻で表示する。</summary>
    public FileRowViewModel(FileEntry entry, TimeZoneInfo timeZone, bool showsPermissions = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(timeZone);

        Entry = entry;
        ShowsPermissions = showsPermissions;
        Kind = FileKinds.Of(entry);
        SizeText = entry.IsDirectory ? "" : DisplayText.FileSize(entry.Size);
        ModifiedText = entry.Modified is { } modified
            ? TimeZoneInfo.ConvertTime(modified, timeZone).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)
            : "";
    }

    /// <summary>元の項目。</summary>
    public FileEntry Entry { get; }

    /// <summary>名前。</summary>
    public string Name => Entry.Name;

    /// <summary>フォルダか。</summary>
    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>アイコンの種類。</summary>
    public FileKind Kind { get; }

    /// <summary>大きさ（フォルダは空）。</summary>
    public string SizeText { get; }

    /// <summary>更新日時。</summary>
    public string ModifiedText { get; }

    /// <summary>属性（サーバー側だけ）。</summary>
    public string Permissions => Entry.Permissions ?? "";

    /// <summary>属性の列を出すか（サーバー側の一覧）。</summary>
    public bool ShowsPermissions { get; }

    /// <summary>ファイルか（フォルダでないか）。</summary>
    public bool IsFile => !IsDirectory;

    /// <summary>読み上げ用の説明。</summary>
    public string AccessibleName => IsDirectory ? $"フォルダ {Name}" : $"{Name}、{SizeText}、{ModifiedText}";

    // アイコンの出し分け（画面の x:Bind で Visibility に使う）
    public bool IsFolder => Kind == FileKind.Folder;

    public bool IsCode => Kind == FileKind.Code;

    public bool IsImage => Kind == FileKind.Image;

    public bool IsArchive => Kind == FileKind.Archive;

    public bool IsText => Kind is FileKind.Text or FileKind.Document;

    public bool IsMedia => Kind == FileKind.Media;

    public bool IsOther => Kind == FileKind.Other;

    /// <summary>一覧の項目を読み上げるときの名前になる。</summary>
    public override string ToString() => AccessibleName;
}

/// <summary>一覧の並べ替えの基準。</summary>
public enum FileSortColumn
{
    /// <summary>名前。</summary>
    Name,

    /// <summary>大きさ。</summary>
    Size,

    /// <summary>更新日時。</summary>
    Modified,
}

/// <summary>
/// 一覧の並べ替え。フォルダはいつも先頭に置き、同じ値どうしは名前順にする。
/// </summary>
internal sealed class FileRowComparer(FileSortColumn column, bool descending) : IComparer<FileRowViewModel>
{
    public int Compare(FileRowViewModel? x, FileRowViewModel? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;
        if (x.IsDirectory != y.IsDirectory)
            return x.IsDirectory ? -1 : 1;

        var result = column switch
        {
            FileSortColumn.Size when !x.IsDirectory => x.Entry.Size.CompareTo(y.Entry.Size),
            FileSortColumn.Modified => Nullable.Compare(x.Entry.Modified, y.Entry.Modified),
            _ => 0,
        };
        if (result == 0)
            result = NaturalStringComparer.Instance.Compare(x.Name, y.Name);
        return descending ? -result : result;
    }
}
