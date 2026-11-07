using Hakobune.Core.Storage;
using Hakobune.Presentation.Updates;

namespace Hakobune.Presentation.Settings;

/// <summary>画面の色。</summary>
public enum ThemeChoice
{
    /// <summary>Windows の設定に合わせる。</summary>
    System,

    /// <summary>ライト。</summary>
    Light,

    /// <summary>ダーク。</summary>
    Dark,
}

/// <summary>ウインドウの位置と大きさ（画面の画素）。</summary>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool IsMaximized);

/// <summary>アプリの設定と、前回の画面の様子（%APPDATA%\Hakobune\settings.json）。</summary>
public sealed record AppSettings
{
    /// <summary>画面の色。</summary>
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>パソコン側で隠しファイルを出すか（Windows が守っているシステムのファイルは出さない）。</summary>
    public bool ShowHiddenFiles { get; init; }

    /// <summary>ファイルの一覧の行の高さを詰めるか（標準 34px、コンパクト 28px）。</summary>
    public bool UseCompactRows { get; init; }

    /// <summary>前回のウインドウの位置と大きさ。</summary>
    public WindowPlacement? Window { get; init; }

    /// <summary>前回パソコン側で開いていたフォルダ。</summary>
    public string? LastLocalFolder { get; init; }

    /// <summary>接続先の一覧を開いていたか。</summary>
    public bool IsSidebarOpen { get; init; } = true;

    /// <summary>転送の一覧を広げていたか。</summary>
    public bool IsTransferListExpanded { get; init; } = true;

    /// <summary>転送の一覧を広げたときの高さ。</summary>
    public double TransferListHeight { get; init; } = 244;

    /// <summary>ファイルの一覧の列の幅。</summary>
    public FileColumnWidths Columns { get; init; } = new();

    /// <summary>送らないファイル。</summary>
    public ExclusionSettings Exclusions { get; init; } = new();

    /// <summary>上書き・削除の前の控え。</summary>
    public BackupSettings Backups { get; init; } = new();

    /// <summary>知らせ方（Windows の通知と、タスクバーのボタン）。</summary>
    public NotificationSettings Notifications { get; init; } = new();

    /// <summary>新しい版を知らせるかと、前に確かめた結果。</summary>
    public UpdateSettings Updates { get; init; } = new();

    /// <summary>読み込む。無い・読めない・壊れているときは、既定の設定にする。</summary>
    public static async Task<AppSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await JsonFile.LoadAsync<AppSettings>(path, cancellationToken: cancellationToken).ConfigureAwait(false) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>保存する（アプリを閉じるときに、その場で書く）。書けなければ false。</summary>
    public static bool TrySave(string path, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            JsonFile.Save(path, settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
