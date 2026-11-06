using System.Globalization;
using System.Text.Json.Serialization;
using Hakobune.Core.Storage;

namespace Hakobune.Core.Connections;

/// <summary>接続の方式。</summary>
public enum ConnectionProtocol
{
    /// <summary>FTP（暗号化なし）。</summary>
    Ftp,

    /// <summary>FTPS（明示的。接続してから暗号化する。FTPES とも呼ぶ）。</summary>
    FtpsExplicit,

    /// <summary>FTPS（暗黙的。最初から暗号化する。ふつうはポート 990）。</summary>
    FtpsImplicit,

    /// <summary>SFTP（SSH）。</summary>
    Sftp,

    /// <summary>
    /// 自動（FFFTP のように選ばなくてよい）。暗号化（FTPS・明示的）を先に試し、サーバーが暗号化に対応していなければ、
    /// 暗号化なしの FTP でつなぐ。つながったら、その方式をこの接続先に覚える。
    /// </summary>
    Auto,
}

/// <summary>
/// 保存しておく接続先。パスワードはここには入れない（Windows の資格情報マネージャーに入れる）。
/// </summary>
public sealed record ConnectionProfile
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>接続先の ID（パスワードや信頼した鍵との結び付けに使う）。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>表示名。空ならホスト名を出す。</summary>
    public string Name { get; init; } = "";

    /// <summary>まとまりの名前（左の一覧の見出し）。空なら「接続先」。</summary>
    public string Group { get; init; } = "";

    public ConnectionProtocol Protocol { get; init; } = ConnectionProtocol.Sftp;

    public string Host { get; init; } = "";

    /// <summary>ポート番号。0 なら方式ごとの既定。</summary>
    public int Port { get; init; }

    public string UserName { get; init; } = "";

    /// <summary>SFTP の秘密鍵ファイル。</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>接続したときに開くサーバーのフォルダ。空ならログインしたときのフォルダ。</summary>
    public string RemoteDirectory { get; init; } = "";

    /// <summary>接続したときに開くパソコンのフォルダ。空なら変えない。</summary>
    public string LocalDirectory { get; init; } = "";

    /// <summary>ファイル名の文字コード（utf-8、shift_jis、euc-jp）。</summary>
    public string FileNameEncoding { get; init; } = "utf-8";

    /// <summary>FTP のパッシブモード。</summary>
    public bool UsePassiveMode { get; init; } = true;

    /// <summary>同時に転送する数の上限。</summary>
    public int MaxConcurrency { get; init; } = 3;

    /// <summary>
    /// この接続先だけで送らない名前。1 行に 1 つ（設定の「送らないファイル」に足して使う。書き方は <see cref="Hakobune.Core.Transfers.ExclusionRules"/>）。
    /// </summary>
    public string ExcludePatterns { get; init; } = "";

    /// <summary><see cref="ExcludePatterns"/> を 1 つずつに分けたもの（空の行は除く）。</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ExcludePatternList =>
        [.. ExcludePatterns.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>実際に使うポート番号。</summary>
    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : DefaultPort(Protocol);

    /// <summary>暗号化する方式か。</summary>
    [JsonIgnore]
    public bool IsEncrypted => Protocol != ConnectionProtocol.Ftp;

    /// <summary>一覧に出す名前。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    /// <summary>方式ごとの既定のポート番号。</summary>
    public static int DefaultPort(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Sftp => 22,
        ConnectionProtocol.FtpsImplicit => 990,
        _ => 21,
    };

    /// <summary>方式の短い名前（FTP、FTPS、SFTP。自動でまだ決まっていなければ「FTP（自動）」）。</summary>
    public static string ProtocolLabel(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Ftp => "FTP",
        ConnectionProtocol.Sftp => "SFTP",
        ConnectionProtocol.Auto => "FTP（自動）",
        _ => "FTPS",
    };

    /// <summary>
    /// 「sftp://user@example.com:22/var/www」のような書き方から接続先を作る（クイック接続やコマンドライン用）。
    /// ftp（自動。暗号化できれば暗号化する）、ftpes（明示的 FTPS）、ftps（暗黙的 FTPS）、sftp に対応する。
    /// URL にパスワードがあれば <paramref name="password"/> に返す。
    /// </summary>
    public static bool TryParseUrl(string text, out ConnectionProfile profile, out string? password)
    {
        profile = new ConnectionProfile();
        password = null;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
            return false;

        ConnectionProtocol? protocol = uri.Scheme.ToLowerInvariant() switch
        {
            "ftp" => ConnectionProtocol.Auto,
            "ftpes" => ConnectionProtocol.FtpsExplicit,
            "ftps" => ConnectionProtocol.FtpsImplicit,
            "sftp" or "ssh" => ConnectionProtocol.Sftp,
            _ => null,
        };
        if (protocol is null || string.IsNullOrEmpty(uri.Host))
            return false;

        var userName = "";
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            userName = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 2)
                password = Uri.UnescapeDataString(parts[1]);
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        profile = new ConnectionProfile
        {
            Protocol = protocol.Value,
            Host = uri.DnsSafeHost,
            Port = uri.Port > 0 && uri.Port != DefaultPort(protocol.Value) ? uri.Port : 0,
            UserName = userName,
            RemoteDirectory = path is "/" or "" ? "" : path,
        };
        return true;
    }

    /// <summary>「SFTP・example.com」のような説明。既定でないポートは付ける。</summary>
    public string Describe() =>
        Port > 0 && Port != DefaultPort(Protocol)
            ? $"{ProtocolLabel(Protocol)}・{Host}:{Port.ToString(CultureInfo.InvariantCulture)}"
            : $"{ProtocolLabel(Protocol)}・{Host}";
}

/// <summary>接続先の一覧を保存し、読み込む。</summary>
public static class ConnectionStore
{
    private const int CurrentVersion = 1;

    /// <summary>保存する。</summary>
    public static Task SaveAsync(string path, IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return JsonFile.SaveAsync(path, new Document(CurrentVersion, profiles), cancellationToken: cancellationToken);
    }

    /// <summary>読み込む。ファイルが無い、または壊れていれば空。</summary>
    public static async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var document = await JsonFile.LoadAsync<Document>(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document?.Connections ?? [];
    }

    internal sealed record Document(int Version, IReadOnlyList<ConnectionProfile> Connections);
}
