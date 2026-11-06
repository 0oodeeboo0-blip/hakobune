using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Backups;
using Hakobune.Core.Text;

namespace Hakobune.Presentation.Backups;

/// <summary>控えから元に戻すときに聞く画面。</summary>
public interface IBackupDialogs
{
    /// <summary>
    /// 元に戻すか聞く。<paramref name="names"/> は戻すものの名前（先頭のいくつか）、<paramref name="count"/> は全部の数。
    /// <paramref name="changed"/> は、控えを取ったあとにサーバーで変わったもの（戻すと、いまの中身は控えに回る）。戻すなら true。
    /// </summary>
    Task<bool> ConfirmRestoreAsync(IReadOnlyList<string> names, int count, IReadOnlyList<string> changed);
}

/// <summary>
/// 下の一覧の「控え」。いまの接続先の控えを、操作（同期 1 回など）ごとにまとめて新しい順に並べる。
/// 元に戻すのは <see cref="RestoreRequested"/> で頼む（接続と送信待ちを持っている <see cref="MainViewModel"/> が受け持つ）。
/// </summary>
public sealed class BackupListViewModel : ObservableObject
{
    /// <summary>1 つの操作で並べる行の数の上限（多いときは「ほか N 件」）。</summary>
    public const int MaxRowsPerGroup = 50;

    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private Guid? _connectionId;
    private int _refreshPending;

    /// <summary><paramref name="store"/> の控えを出す（null なら何も出さない）。</summary>
    public BackupListViewModel(BackupStore? store, TimeProvider? timeProvider = null, SynchronizationContext? context = null)
    {
        Store = store;
        _time = timeProvider ?? TimeProvider.System;
        _context = context;
        if (store is not null)
            store.Changed += (_, _) => QueueRefresh();
        Refresh();
    }

    /// <summary>控えの置き場（無ければ null）。</summary>
    public BackupStore? Store { get; }

    /// <summary>操作ごとのまとまり（新しいものから）。</summary>
    public ObservableList<BackupGroupViewModel> Groups { get; } = [];

    /// <summary>控えが 1 つでもあるか。</summary>
    public bool HasGroups { get; private set => SetProperty(ref field, value); }

    /// <summary>控えが無いときの案内。</summary>
    public string EmptyText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>まとめ（「控え 12 件・3.4 MB。30 日・1 GB まで残します」）。</summary>
    public string SummaryText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>控えの置き場のフォルダ（エクスプローラーで開くため）。</summary>
    public string? FolderPath => Store?.Root;

    /// <summary>元に戻すよう頼まれた。</summary>
    public event EventHandler<IReadOnlyList<BackupEntry>>? RestoreRequested;

    /// <summary>出す接続先を変える（つないだとき・切ったとき）。null なら何も出さない。</summary>
    public void SetConnection(Guid? connectionId)
    {
        if (_connectionId == connectionId)
            return;
        _connectionId = connectionId;
        Refresh();
    }

    /// <summary>出し直す。</summary>
    public void Refresh()
    {
        IReadOnlyList<BackupEntry> entries = _connectionId is { } id && Store is not null ? Store.GetEntries(id) : [];
        var groups = new List<BackupGroupViewModel>();
        // 操作ごとにまとめる（新しい順に並んでいるので、最初に出てきた順がそのまま新しい順）
        foreach (var group in entries.GroupBy(entry => entry.Batch ?? entry.Id))
        {
            var members = group.ToList();
            var existing = Groups.FirstOrDefault(item => item.Key == group.Key && item.IsSameAs(members));
            groups.Add(existing ?? new BackupGroupViewModel(this, group.Key, Store?.GetBatch(members[0].Batch), members, _time));
        }
        Groups.SyncTo(groups);
        HasGroups = groups.Count > 0;

        var limits = Store?.Limits;
        EmptyText = Store is null || _connectionId is null
            ? "接続すると、その接続先の控えがここに出ます。"
            : limits is { IsEnabled: false }
                ? "控えを取らない設定になっています（設定の「控え」で変えられます）。"
                : "まだ控えはありません。サーバーのファイルを上書き・削除する前に、元のファイルをここに残します。";
        var size = entries.Sum(entry => entry.Size);
        var keep = limits is null ? "" : $"{(int)limits.KeepFor.TotalDays} 日・{DisplayText.Size(limits.MaxTotalBytes)}まで残します";
        SummaryText = entries.Count == 0 ? keep : $"控え {entries.Count:N0} 件・{DisplayText.Size(size)}。{keep}";
    }

    internal void RequestRestore(IReadOnlyList<BackupEntry> entries)
    {
        if (entries.Count > 0)
            RestoreRequested?.Invoke(this, entries);
    }

    internal void Delete(IReadOnlyList<BackupEntry> entries) => Store?.Delete(entries.Select(entry => entry.Id));

    // 控えはどのスレッドからも増えるので、画面のスレッドでまとめて 1 回出し直す
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 1)
            return;
        if (_context is null)
        {
            Run();
            return;
        }
        _context.Post(_ => Run(), null);

        void Run()
        {
            Volatile.Write(ref _refreshPending, 0);
            Refresh();
        }
    }

    internal static string FormatTime(DateTimeOffset time, TimeProvider timeProvider)
    {
        var zone = timeProvider.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(time, zone);
        var thisYear = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).Year;
        return local.ToString(local.Year == thisYear ? "MM/dd HH:mm" : "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }
}

/// <summary>控えのまとまり（同期 1 回、削除 1 回など）。</summary>
public sealed class BackupGroupViewModel
{
    private readonly BackupListViewModel _owner;

    internal BackupGroupViewModel(BackupListViewModel owner, Guid key, BackupBatch? batch, IReadOnlyList<BackupEntry> entries, TimeProvider time)
    {
        _owner = owner;
        Key = key;
        Entries = entries;
        Title = batch?.Label ?? DescribeKind(entries[0].Kind);
        WhenText = BackupListViewModel.FormatTime(batch?.StartedAt ?? entries.Max(entry => entry.TakenAt), time);
        var parts = new List<string>(4);
        var overwritten = entries.Count(entry => entry.Kind == BackupKind.ServerOverwritten);
        var deleted = entries.Count(entry => entry.Kind == BackupKind.ServerDeleted);
        var local = entries.Count(entry => entry.Kind == BackupKind.LocalOverwritten);
        if (overwritten > 0)
            parts.Add($"上書き {overwritten:N0}");
        if (deleted > 0)
            parts.Add($"削除 {deleted:N0}");
        if (local > 0)
            parts.Add($"パソコンで上書き {local:N0}");
        parts.Add(DisplayText.Size(entries.Sum(entry => entry.Size)));
        SummaryText = string.Join("・", parts);
        Rows = [.. entries.Take(BackupListViewModel.MaxRowsPerGroup).Select(entry => new BackupRowViewModel(owner, entry, time))];
        MoreText = entries.Count > Rows.Count ? $"ほか {entries.Count - Rows.Count:N0} 件（まとめて元に戻せます）" : "";
        RestoreAllText = entries.Count == 1 ? "元に戻す" : $"{entries.Count:N0} 件を元に戻す";
        RestoreAllCommand = new RelayCommand(() => _owner.RequestRestore(Entries));
        DeleteCommand = new RelayCommand(() => _owner.Delete(Entries));
    }

    /// <summary>まとまりの ID（操作の ID）。</summary>
    public Guid Key { get; }

    /// <summary>このまとまりの控え（新しいものから）。</summary>
    public IReadOnlyList<BackupEntry> Entries { get; }

    /// <summary>操作の名前（「同期（パソコン → サーバー）」）。</summary>
    public string Title { get; }

    /// <summary>操作の時刻。</summary>
    public string WhenText { get; }

    /// <summary>まとめ（「上書き 3・削除 2・1.2 MB」）。</summary>
    public string SummaryText { get; }

    /// <summary>並べる行（多いときは先頭だけ）。</summary>
    public IReadOnlyList<BackupRowViewModel> Rows { get; }

    /// <summary>並べなかったぶん（「ほか 120 件」）。無ければ空。</summary>
    public string MoreText { get; }

    /// <summary>並べなかったぶんがあるか。</summary>
    public bool HasMore => MoreText.Length > 0;

    /// <summary>まとめて元に戻すボタンの文。</summary>
    public string RestoreAllText { get; }

    /// <summary>まとめて元に戻す。</summary>
    public IRelayCommand RestoreAllCommand { get; }

    /// <summary>このまとまりの控えを消す。</summary>
    public IRelayCommand DeleteCommand { get; }

    /// <summary>読み上げ用の名前。</summary>
    public string AccessibleName => $"{WhenText} {Title}、{SummaryText}";

    internal bool IsSameAs(IReadOnlyList<BackupEntry> entries) =>
        entries.Count == Entries.Count && entries.Select(entry => entry.Id).SequenceEqual(Entries.Select(entry => entry.Id));

    internal static string DescribeKind(BackupKind kind) => kind switch
    {
        BackupKind.ServerOverwritten => "上書き",
        BackupKind.ServerDeleted => "削除",
        _ => "パソコンで上書き",
    };
}

/// <summary>控え 1 つ。</summary>
public sealed class BackupRowViewModel
{
    internal BackupRowViewModel(BackupListViewModel owner, BackupEntry entry, TimeProvider time)
    {
        Entry = entry;
        var path = entry.OriginalPath.TrimEnd('/', '\\');
        var index = path.LastIndexOfAny(['/', '\\']);
        Name = index < 0 ? path : path[(index + 1)..];
        Folder = index <= 0 ? (entry.IsOnServer ? "/" : "") : path[..index];
        KindText = entry.Kind switch
        {
            BackupKind.ServerOverwritten => "上書き前",
            BackupKind.ServerDeleted => "削除前",
            _ => "パソコン",
        };
        SizeText = DisplayText.FileSize(entry.Size);
        WhenText = entry.OriginalModified is { } modified ? BackupListViewModel.FormatTime(modified, time) : "";
        RestoreCommand = new RelayCommand(() => owner.RequestRestore([Entry]));
    }

    /// <summary>控え。</summary>
    public BackupEntry Entry { get; }

    /// <summary>元のファイルの名前。</summary>
    public string Name { get; }

    /// <summary>元のファイルのフォルダ。</summary>
    public string Folder { get; }

    /// <summary>印（「上書き前」「削除前」「パソコン」）。</summary>
    public string KindText { get; }

    /// <summary>消したファイルの控えか（印を赤くする）。</summary>
    public bool IsDeleted => Entry.Kind == BackupKind.ServerDeleted;

    /// <summary>上書きしたファイルの控えか。</summary>
    public bool IsOverwritten => Entry.Kind != BackupKind.ServerDeleted;

    /// <summary>大きさ。</summary>
    public string SizeText { get; }

    /// <summary>元のファイルの更新日時。</summary>
    public string WhenText { get; }

    /// <summary>元に戻す。</summary>
    public IRelayCommand RestoreCommand { get; }

    /// <summary>読み上げ用の名前。</summary>
    public string AccessibleName => $"{Name}、{KindText}、{Folder}";
}
