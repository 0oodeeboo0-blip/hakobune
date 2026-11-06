using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Hakobune.Core.Connections;

namespace Hakobune.Presentation.Connections;

/// <summary>接続先を作る・直したときの結果。</summary>
/// <param name="Profile">接続先。</param>
/// <param name="Password">入れたパスワード。空欄のままなら null（保存してあるものを使う）。</param>
/// <param name="SavePassword">パスワードを保存するか。</param>
public sealed record ConnectionEditResult(ConnectionProfile Profile, string? Password, bool SavePassword)
{
    /// <summary>パスワードがログなどに出ないようにする。</summary>
    public override string ToString() => $"{nameof(ConnectionEditResult)} {{ Profile = {Profile.DisplayName} }}";
}

/// <summary>接続先の作成・編集・削除の確認（画面がダイアログで出す）。</summary>
public interface IConnectionDialogs
{
    /// <summary>接続先を作る（<paramref name="existing"/> が null）か、直す。やめたら null。</summary>
    Task<ConnectionEditResult?> EditAsync(ConnectionProfile? existing, bool hasSavedPassword);

    /// <summary>保存せずに接続する先（クイック接続）を聞く。やめたら null。</summary>
    Task<ConnectionEditResult?> AskQuickConnectAsync();

    /// <summary>接続先を消してよいか聞く。</summary>
    Task<bool> ConfirmDeleteAsync(ConnectionProfile profile);
}

/// <summary>接続先の作成・編集の画面。</summary>
public sealed class ConnectionEditorViewModel : ObservableObject
{
    private static readonly ConnectionProtocol[] Protocols =
    [
        ConnectionProtocol.Sftp,
        ConnectionProtocol.FtpsExplicit,
        ConnectionProtocol.FtpsImplicit,
        ConnectionProtocol.Ftp,
    ];

    private static readonly string[] Encodings = ["utf-8", "shift_jis", "euc-jp"];

    private readonly Guid _id;
    private readonly int _maxConcurrency;
    private bool _applyingUrl;

    /// <summary>
    /// 画面を作る。<paramref name="profile"/> が null なら新しい接続先。<paramref name="hasSavedPassword"/> は、保存したパスワードがあるか。
    /// <paramref name="quickConnect"/> なら、保存せずに接続する（表示名やパスワードの保存は聞かない）。
    /// </summary>
    public ConnectionEditorViewModel(ConnectionProfile? profile = null, bool hasSavedPassword = false, bool quickConnect = false)
    {
        var source = profile ?? new ConnectionProfile();
        _id = source.Id;
        _maxConcurrency = source.MaxConcurrency;
        IsNew = profile is null;
        IsQuickConnect = quickConnect;
        HasSavedPassword = hasSavedPassword;
        // 前に「保存しない」を選んだ接続先は、そのまま保存しないでおく
        SavePassword = !quickConnect && (IsNew || hasSavedPassword);
        Name = source.Name;
        Group = source.Group;
        ProtocolIndex = Math.Max(0, Array.IndexOf(Protocols, source.Protocol));
        Host = source.Host;
        PortText = source.Port > 0 ? source.Port.ToString(CultureInfo.InvariantCulture) : "";
        UserName = source.UserName;
        PrivateKeyPath = source.PrivateKeyPath ?? "";
        RemoteDirectory = source.RemoteDirectory;
        LocalDirectory = source.LocalDirectory;
        EncodingIndex = Math.Max(0, Array.IndexOf(Encodings, source.FileNameEncoding.ToLowerInvariant()));
        UsePassiveMode = source.UsePassiveMode;
        OpensAdvancedSettings = RemoteDirectory.Length > 0 || LocalDirectory.Length > 0 || EncodingIndex != 0 || !UsePassiveMode;
    }

    /// <summary>方式の選択肢。</summary>
    public static IReadOnlyList<string> ProtocolChoices { get; } =
        ["SFTP（おすすめ）", "FTPS（明示的・FTPES）", "FTPS（暗黙的）", "FTP（暗号化なし）"];

    /// <summary>ファイル名の文字コードの選択肢。</summary>
    public static IReadOnlyList<string> EncodingChoices { get; } = ["UTF-8（ふつうはこれ）", "Shift_JIS（古いサーバー）", "EUC-JP（古いサーバー）"];

    /// <summary>新しい接続先か。</summary>
    public bool IsNew { get; }

    /// <summary>保存せずに接続するか（クイック接続）。</summary>
    public bool IsQuickConnect { get; }

    /// <summary>表示名・まとまり・パスワードの保存の欄を出すか。</summary>
    public bool ShowsSaveOptions => !IsQuickConnect;

    /// <summary>画面の見出し。</summary>
    public string Title => IsQuickConnect ? "クイック接続" : IsNew ? "新しい接続" : "接続先の編集";

    /// <summary>決定のボタンの文字。</summary>
    public string PrimaryButtonText => IsQuickConnect ? "接続" : IsNew ? "保存して接続" : "保存";

    /// <summary>保存したパスワードがあるか。</summary>
    public bool HasSavedPassword { get; }

    /// <summary>「詳しい設定」を開いておくか（既定から変えた設定があるとき）。</summary>
    public bool OpensAdvancedSettings { get; }

    public string Name { get; set => SetProperty(ref field, value ?? ""); } = "";

    /// <summary>表示名の欄に薄く出す説明（空欄ならホスト名を使う）。</summary>
    public string NamePlaceholder => string.IsNullOrWhiteSpace(Host) ? "空欄ならホスト名" : Host.Trim();

    public string Group { get; set => SetProperty(ref field, value ?? ""); } = "";

    /// <summary>方式（<see cref="ProtocolChoices"/> の番号）。</summary>
    public int ProtocolIndex
    {
        get;
        set
        {
            if (!SetProperty(ref field, Math.Clamp(value, 0, Protocols.Length - 1)))
                return;
            OnPropertyChanged(nameof(Protocol));
            OnPropertyChanged(nameof(IsSftp));
            OnPropertyChanged(nameof(IsFtp));
            OnPropertyChanged(nameof(ShowsInsecureWarning));
            OnPropertyChanged(nameof(PortPlaceholder));
            OnPropertyChanged(nameof(UserNamePlaceholder));
            OnValidationChanged();
        }
    }

    /// <summary>方式。</summary>
    public ConnectionProtocol Protocol => Protocols[ProtocolIndex];

    /// <summary>SFTP か（秘密鍵の欄を出す）。</summary>
    public bool IsSftp => Protocol == ConnectionProtocol.Sftp;

    /// <summary>FTP 系か（パッシブモードの欄を出す）。</summary>
    public bool IsFtp => !IsSftp;

    /// <summary>暗号化しない方式を選んだか（注意を出す）。</summary>
    public bool ShowsInsecureWarning => Protocol == ConnectionProtocol.Ftp;

    /// <summary>
    /// ホスト名。「sftp://user@example.com:22/var/www」のような URL を貼り付けると、ほかの欄も埋める。
    /// </summary>
    public string Host
    {
        get;
        set
        {
            if (!SetProperty(ref field, value ?? ""))
                return;
            if (!_applyingUrl && field.Contains("://", StringComparison.Ordinal))
                ApplyUrl(field);
            OnPropertyChanged(nameof(NamePlaceholder));
            OnValidationChanged();
        }
    } = "";

    /// <summary>ポート番号（空なら方式ごとの既定）。</summary>
    public string PortText
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
                OnValidationChanged();
        }
    } = "";

    /// <summary>ポート番号の欄に薄く出す既定の値。</summary>
    public string PortPlaceholder => ConnectionProfile.DefaultPort(Protocol).ToString(CultureInfo.InvariantCulture);

    public string UserName
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
                OnValidationChanged();
        }
    } = "";

    /// <summary>ユーザー名の欄に薄く出す説明。</summary>
    public string UserNamePlaceholder => IsFtp ? "空欄なら anonymous（匿名）で入ります" : "";

    /// <summary>パスワード。空のままなら、保存してあるものを使う。</summary>
    public string Password { get; set => SetProperty(ref field, value ?? ""); } = "";

    /// <summary>パスワードの欄に薄く出す説明。</summary>
    public string PasswordPlaceholder => HasSavedPassword ? "保存してあるパスワードを使う" : "";

    /// <summary>パスワードを保存するか（Windows の資格情報マネージャーに入れる）。</summary>
    public bool SavePassword { get; set => SetProperty(ref field, value); } = true;

    public string PrivateKeyPath { get; set => SetProperty(ref field, value ?? ""); } = "";

    public string RemoteDirectory { get; set => SetProperty(ref field, value ?? ""); } = "";

    public string LocalDirectory { get; set => SetProperty(ref field, value ?? ""); } = "";

    /// <summary>ファイル名の文字コード（<see cref="EncodingChoices"/> の番号）。</summary>
    public int EncodingIndex { get; set => SetProperty(ref field, Math.Clamp(value, 0, Encodings.Length - 1)); }

    public bool UsePassiveMode { get; set => SetProperty(ref field, value); } = true;

    /// <summary>入力の問題（なければ空）。</summary>
    public string ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Host))
                return "ホスト名を入れてください";
            if (Host.Trim().Contains(' ', StringComparison.Ordinal))
                return "ホスト名に空白は使えません";
            if (PortText.Trim().Length > 0 && ParsePort() is null)
                return "ポート番号は 1〜65535 の数で入れてください";
            if (IsSftp && string.IsNullOrWhiteSpace(UserName))
                return "SFTP ではユーザー名が要ります";
            return "";
        }
    }

    /// <summary>保存できるか。</summary>
    public bool IsValid => ValidationMessage.Length == 0;

    /// <summary>
    /// 入力の問題を画面に出すか。ホスト名がまだ空のうちは、決定のボタンを押せなくするだけにして、文は出さない。
    /// </summary>
    public bool ShowsValidationMessage => !IsValid && !string.IsNullOrWhiteSpace(Host);

    /// <summary>入力した内容から接続先を作る。</summary>
    public ConnectionProfile ToProfile() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Group = Group.Trim(),
        Protocol = Protocol,
        Host = Host.Trim(),
        Port = ParsePort() ?? 0,
        UserName = UserName.Trim(),
        PrivateKeyPath = IsSftp && PrivateKeyPath.Trim().Length > 0 ? PrivateKeyPath.Trim() : null,
        RemoteDirectory = RemoteDirectory.Trim(),
        LocalDirectory = LocalDirectory.Trim(),
        FileNameEncoding = Encodings[EncodingIndex],
        UsePassiveMode = UsePassiveMode,
        MaxConcurrency = _maxConcurrency,
    };

    /// <summary>入力した内容を結果にする。</summary>
    public ConnectionEditResult ToResult() =>
        new(ToProfile(), Password.Length > 0 ? Password : null, SavePassword && !IsQuickConnect);

    private int? ParsePort()
    {
        var text = PortText.Trim();
        if (text.Length == 0)
            return null;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : null;
    }

    private void ApplyUrl(string url)
    {
        if (!ConnectionProfile.TryParseUrl(url, out var parsed, out var password))
            return;
        _applyingUrl = true;
        try
        {
            ProtocolIndex = Array.IndexOf(Protocols, parsed.Protocol);
            Host = parsed.Host;
            PortText = parsed.Port > 0 ? parsed.Port.ToString(CultureInfo.InvariantCulture) : "";
            if (parsed.UserName.Length > 0)
                UserName = parsed.UserName;
            if (password is not null)
                Password = password;
            if (parsed.RemoteDirectory.Length > 0)
                RemoteDirectory = parsed.RemoteDirectory;
        }
        finally
        {
            _applyingUrl = false;
        }
    }

    private void OnValidationChanged()
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ShowsValidationMessage));
    }
}
