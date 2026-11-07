using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Connections;
using Hakobune.Core.Import;

namespace Hakobune.Presentation.Connections;

/// <summary>FFFTP から接続先を読み込むときの画面とのやり取り。</summary>
public interface IFfftpImportDialogs
{
    /// <summary>このパソコンで使っていた FFFTP の設定（レジストリ）。無ければ null。</summary>
    FfftpSettings? ReadInstalledSettings();

    /// <summary>FFFTP の設定のファイル（ffftp.ini か、「設定をファイルに保存」した .reg）を選んでもらう。やめたら null。</summary>
    Task<string?> PickSettingsFileAsync();

    /// <summary>読み込む接続先を選んでもらう。読み込むなら true。</summary>
    Task<bool> ReviewAsync(FfftpImportViewModel import);
}

/// <summary>FFFTP から読み込む接続先を選ぶ画面（ダイアログの中身）。</summary>
public sealed class FfftpImportViewModel : ObservableObject
{
    private readonly IReadOnlyList<ConnectionProfile> _existing;
    private readonly Func<Task<string?>>? _pickFile;
    private FfftpSettings _settings = new();
    private string? _masterPassword;

    /// <summary><paramref name="existing"/> は、もう登録してある接続先（同じものは、はじめは選ばない）。</summary>
    public FfftpImportViewModel(IReadOnlyList<ConnectionProfile> existing, Func<Task<string?>>? pickFile = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        _existing = existing;
        _pickFile = pickFile;
        CheckMasterPasswordCommand = new RelayCommand(CheckMasterPassword);
        ChooseFileCommand = new AsyncRelayCommand(ChooseFileAsync, () => _pickFile is not null);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
    }

    /// <summary>どこから読んだか（例: このパソコンの FFFTP の設定）。</summary>
    public string SourceText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>読み込める接続先（FFFTP の一覧の順）。</summary>
    public IReadOnlyList<FfftpImportRow> Rows { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>接続先があるか。</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>接続先が無いときの説明。</summary>
    public string EmptyText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>読めない・読み込めないことの説明（無ければ空）。</summary>
    public string ProblemText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>説明があるか。</summary>
    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>接続先が無いことを出すか。</summary>
    public bool ShowsEmptyText => !HasRows && !HasProblem && EmptyText.Length > 0;

    /// <summary>パスワードの説明を、一覧の下に出すか（マスターパスワードを聞くときは、その上に出す）。</summary>
    public bool ShowsPasswordNoteBelow => !NeedsMasterPassword && PasswordNote.Length > 0;

    /// <summary>マスターパスワードを入れてもらうか（入れないとパスワードは読み込まない）。</summary>
    public bool NeedsMasterPassword { get; private set => SetProperty(ref field, value); }

    /// <summary>入れてもらったマスターパスワード。</summary>
    public string MasterPassword { get; set => SetProperty(ref field, value); } = "";

    /// <summary>マスターパスワードについての知らせ（違っていた、など）。</summary>
    public string MasterPasswordMessage { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>パスワードをどう扱うかの説明。</summary>
    public string PasswordNote { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>選んだ数。</summary>
    public int SelectedCount { get; private set => SetProperty(ref field, value); }

    /// <summary>読み込むボタンの文字（例: 12 件を読み込む）。</summary>
    public string PrimaryText => SelectedCount > 0 ? $"{SelectedCount} 件を読み込む" : "読み込む";

    /// <summary>読み込めるか。</summary>
    public bool CanImport => SelectedCount > 0;

    /// <summary>マスターパスワードを確かめる。</summary>
    public IRelayCommand CheckMasterPasswordCommand { get; }

    /// <summary>設定のファイルを選んで読み直す。</summary>
    public IAsyncRelayCommand ChooseFileCommand { get; }

    /// <summary>すべて選ぶ。</summary>
    public IRelayCommand SelectAllCommand { get; }

    /// <summary>すべて外す。</summary>
    public IRelayCommand SelectNoneCommand { get; }

    /// <summary>設定を読む。</summary>
    public void Load(FfftpSettings settings, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _masterPassword = null;
        MasterPassword = "";
        MasterPasswordMessage = "";
        SourceText = sourceText;
        var list = FfftpHosts.Read(settings);
        NeedsMasterPassword = list.NeedsMasterPassword;
        Rows = [.. list.Hosts.Select(host => new FfftpImportRow(host, ToProfile(host), IsExisting(host), OnRowSelectionChanged))];
        OnPropertyChanged(nameof(HasRows));
        ProblemText = list.IsEncrypted
            ? "FFFTP の「設定をすべて暗号化して保存」がオンになっているため、読めません。FFFTP の設定でオフにしてから、もう一度読み込んでください"
            : "";
        OnPropertyChanged(nameof(HasProblem));
        EmptyText = list.IsEncrypted ? "" : "FFFTP の接続先が見つかりません";
        OnPropertyChanged(nameof(ShowsEmptyText));
        UpdatePasswords();
        UpdateSelection();
    }

    /// <summary>設定のファイル（ffftp.ini か .reg）を読む。読めなければ説明を出す。</summary>
    public async Task LoadFileAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            Load(FfftpSettings.Load(bytes), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Load(new FfftpSettings(), Path.GetFileName(path));
            ProblemText = $"ファイルを読めませんでした（{ex.Message}）";
            OnPropertyChanged(nameof(HasProblem));
            OnPropertyChanged(nameof(ShowsEmptyText));
        }
    }

    /// <summary>選んだ接続先を、Hakobune の接続先にする（パスワードは、戻せたものだけ）。</summary>
    public IReadOnlyList<(ConnectionProfile Profile, string? Password)> BuildResult() =>
        [.. Rows.Where(row => row.IsSelected).Select(row => (row.Profile, row.Password))];

    /// <summary>FFFTP の接続先を、Hakobune の接続先にする。</summary>
    public static ConnectionProfile ToProfile(FfftpHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var protocol = ToProtocol(host);
        return new ConnectionProfile
        {
            Name = host.Name.Length > 0 ? host.Name : host.Host,
            Group = string.Join(" / ", host.Groups),
            Protocol = protocol,
            Host = host.Host,
            Port = host.Port > 0 && host.Port != ConnectionProfile.DefaultPort(protocol) ? host.Port : 0,
            UserName = host.UserName,
            LocalDirectory = host.LocalDirectory,
            RemoteDirectory = host.RemoteDirectory,
            FileNameEncoding = host.FileNameKanji switch
            {
                0 => "shift_jis",
                2 => "euc-jp",
                _ => "utf-8",
            },
            UsePassiveMode = host.Passive,
            MaxConcurrency = host.MaxConnections is > 0 and var count ? Math.Clamp(count, 1, 10) : 3,
        };
    }

    // FFFTP は、許した方式を「暗黙的 → 明示的 → 暗号化なし」の順に試す。Hakobune の「自動」は「明示的 → 暗号化なし」
    private static ConnectionProtocol ToProtocol(FfftpHost host)
    {
        if (host.AllowImplicitTls && (host.Port == 990 || (!host.AllowExplicitTls && !host.AllowPlain)))
            return ConnectionProtocol.FtpsImplicit;
        if (host.AllowExplicitTls && !host.AllowPlain)
            return ConnectionProtocol.FtpsExplicit;
        if (host.AllowPlain && !host.AllowExplicitTls && !host.AllowImplicitTls)
            return ConnectionProtocol.Ftp;
        return ConnectionProtocol.Auto;
    }

    private bool IsExisting(FfftpHost host)
    {
        var profile = ToProfile(host);
        return _existing.Any(existing =>
            string.Equals(existing.Host, profile.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.UserName, profile.UserName, StringComparison.Ordinal)
            && existing.EffectivePort == profile.EffectivePort);
    }

    private void CheckMasterPassword()
    {
        if (!FfftpPasswords.CheckMasterPassword(_settings, MasterPassword))
        {
            MasterPasswordMessage = "マスターパスワードが違います";
            return;
        }
        _masterPassword = MasterPassword;
        NeedsMasterPassword = false;
        MasterPasswordMessage = "";
        UpdatePasswords();
    }

    // パスワードを戻す（マスターパスワードが要るのに、まだ入れてもらっていなければ戻さない）
    private void UpdatePasswords()
    {
        var withPassword = 0;
        foreach (var row in Rows)
        {
            row.Password = NeedsMasterPassword ? null : FfftpPasswords.Decode(row.Source.StoredPassword, _masterPassword);
            if (row.Password is { Length: > 0 })
                withPassword++;
        }
        var stored = Rows.Count(row => !string.IsNullOrEmpty(row.Source.StoredPassword));
        PasswordNote = stored == 0 ? ""
            : NeedsMasterPassword ? $"FFFTP にマスターパスワードが決めてあります。入れると、保存されたパスワード（{stored} 件）も読み込みます。入れなければ、接続するときに聞きます"
            : $"保存されたパスワード（{withPassword} 件）は、Windows の資格情報マネージャーに入れます";
        OnPropertyChanged(nameof(ShowsPasswordNoteBelow));
    }

    private void SetAll(bool selected)
    {
        foreach (var row in Rows)
            row.IsSelected = selected;
    }

    private async Task ChooseFileAsync()
    {
        if (_pickFile is null || await _pickFile().ConfigureAwait(true) is not { } path)
            return;
        await LoadFileAsync(path).ConfigureAwait(true);
    }

    private void OnRowSelectionChanged() => UpdateSelection();

    private void UpdateSelection()
    {
        SelectedCount = Rows.Count(row => row.IsSelected);
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(CanImport));
    }
}

/// <summary>読み込める接続先の 1 行。</summary>
public sealed class FfftpImportRow : ObservableObject
{
    private readonly Action _selectionChanged;

    internal FfftpImportRow(FfftpHost source, ConnectionProfile profile, bool isExisting, Action selectionChanged)
    {
        Source = source;
        Profile = profile;
        IsExisting = isExisting;
        _selectionChanged = selectionChanged;
        IsSelected = !isExisting;
    }

    /// <summary>FFFTP の設定に書いてあったもの。</summary>
    public FfftpHost Source { get; }

    /// <summary>Hakobune の接続先にしたもの。</summary>
    public ConnectionProfile Profile { get; }

    /// <summary>同じ接続先が、もう Hakobune にあるか。</summary>
    public bool IsExisting { get; }

    /// <summary>読み込むか。</summary>
    public bool IsSelected
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                _selectionChanged();
        }
    }

    /// <summary>戻せたパスワード（無ければ null）。</summary>
    public string? Password
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(DetailText));
        }
    }

    /// <summary>名前。</summary>
    public string Name => Profile.DisplayName;

    /// <summary>グループ（無ければ空）。</summary>
    public string GroupText => Profile.Group;

    /// <summary>グループがあるか。</summary>
    public bool HasGroup => Profile.Group.Length > 0;

    /// <summary>方式とアドレス（例: FTPS・ftp.example.com:2121・web）。</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>
            {
                ConnectionProfile.ProtocolLabel(Profile.Protocol),
                Profile.Port > 0 ? $"{Profile.Host}:{Profile.Port}" : Profile.Host,
                Source.Anonymous ? "anonymous" : Profile.UserName,
            };
            if (Password is { Length: > 0 })
                parts.Add("パスワードあり");
            return string.Join("・", parts.Where(part => part.Length > 0));
        }
    }

    /// <summary>注意（同じものがある、ファイル名の文字コードを変える、など）。無ければ空。</summary>
    public string NoteText => IsExisting ? "同じ接続先がもうあります"
        : Source.FileNameKanji is 1 or 3 or 4 ? "ファイル名の文字コード（JIS・Samba）は使えないので、UTF-8 にします"
        : "";

    /// <summary>注意があるか。</summary>
    public bool HasNote => NoteText.Length > 0;

    /// <summary>読み上げ用の名前。</summary>
    public string AccessibleName => $"{Name}、{DetailText}{(HasNote ? "、" + NoteText : "")}";
}
