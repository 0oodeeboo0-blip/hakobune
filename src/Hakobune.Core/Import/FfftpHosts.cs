namespace Hakobune.Core.Import;

/// <summary>FFFTP の接続先 1 つ（設定に書いてあるまま。パスワードは暗号化されたまま）。</summary>
/// <param name="Name">接続先の名前（FFFTP の「ホストの設定名」）。</param>
/// <param name="Groups">入っているグループ（外側から順に）。</param>
/// <param name="Host">ホスト名（アドレス）。</param>
/// <param name="Port">ポート番号。</param>
/// <param name="UserName">ユーザー名。</param>
/// <param name="StoredPassword">保存されたパスワード（暗号化されたまま）。</param>
/// <param name="Anonymous">anonymous で入るか。</param>
/// <param name="LocalDirectory">はじめに開くパソコン側のフォルダ。</param>
/// <param name="RemoteDirectory">はじめに開くサーバー側のフォルダ。</param>
/// <param name="Passive">PASV モードを使うか。</param>
/// <param name="AllowPlain">暗号化なしの FTP を許すか。</param>
/// <param name="AllowExplicitTls">FTPS（明示的、FTPES）を使うか。</param>
/// <param name="AllowImplicitTls">FTPS（暗黙的）を使うか。</param>
/// <param name="FileNameKanji">ファイル名の漢字コード（-1 自動、0 Shift_JIS、1 JIS、2 EUC、5〜7 UTF-8）。</param>
/// <param name="MaxConnections">同時に接続する数（書いてあれば）。</param>
public sealed record FfftpHost(
    string Name,
    IReadOnlyList<string> Groups,
    string Host,
    int Port,
    string UserName,
    string? StoredPassword,
    bool Anonymous,
    string LocalDirectory,
    string RemoteDirectory,
    bool Passive,
    bool AllowPlain,
    bool AllowExplicitTls,
    bool AllowImplicitTls,
    int FileNameKanji,
    int? MaxConnections);

/// <summary>FFFTP の設定から読んだ接続先。</summary>
/// <param name="Hosts">接続先（FFFTP の一覧の順）。</param>
/// <param name="IsEncrypted">「設定をすべて暗号化して保存」がオンで、読めない。</param>
/// <param name="NeedsMasterPassword">マスターパスワードを決めてある（入れてもらわないとパスワードを戻せない）。</param>
public sealed record FfftpHostList(IReadOnlyList<FfftpHost> Hosts, bool IsEncrypted, bool NeedsMasterPassword);

/// <summary>FFFTP の設定から接続先を読む（FFFTP 1.98 〜 5.8 の形）。</summary>
public static class FfftpHosts
{
    private const string Options = @"FFFTP\Options";
    private const string DefaultHost = @"FFFTP\Options\DefaultHost";
    private const int GroupFlag = 0x8000;
    private const int LevelMask = 0x7FFF;

    /// <summary>接続先を読む。グループ（フォルダ）は、入っている接続先の <see cref="FfftpHost.Groups"/> になる。</summary>
    public static FfftpHostList Read(FfftpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // 「設定をすべて暗号化して保存」のときは、値ごとに鍵で隠してあるので読まない
        if ((settings.GetInt("FFFTP", "EncryptAll") is { } all && all != 0) || settings.HasKey(@"FFFTP\EncryptedOptions"))
            return new FfftpHostList([], IsEncrypted: true, NeedsMasterPassword: false);

        var count = settings.GetInt(Options, "SetNum") ?? CountHostKeys(settings);
        var hosts = new List<FfftpHost>();
        var groups = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var key = $@"{Options}\Host{i}";
            if (!settings.HasKey(key))
                continue;
            var level = settings.GetInt(key, "Set") ?? 0;
            var depth = level & LevelMask;
            if (groups.Count > depth)
                groups.RemoveRange(depth, groups.Count - depth);
            var name = (settings.GetString(key, "HostName") ?? "").Trim();
            if ((level & GroupFlag) != 0)
            {
                groups.Add(name);
                continue;
            }
            hosts.Add(ReadHost(settings, key, name, [.. groups.Where(group => group.Length > 0)]));
        }

        var needsMaster = hosts.Any(host => !string.IsNullOrEmpty(host.StoredPassword))
            && !FfftpPasswords.CheckMasterPassword(settings, null);
        return new FfftpHostList(hosts, IsEncrypted: false, needsMaster);
    }

    private static FfftpHost ReadHost(FfftpSettings settings, string key, string name, IReadOnlyList<string> groups)
    {
        // FFFTP は「共通の設定（DefaultHost）」と同じ値を書かないので、無ければそちらを見る
        string Text(string value) => settings.GetString(key, value) ?? settings.GetString(DefaultHost, value) ?? "";
        int Number(string value, int fallback) => settings.GetInt(key, value) ?? settings.GetInt(DefaultHost, value) ?? fallback;

        var anonymous = Number("Anonymous", 0) != 0;
        return new FfftpHost(
            Name: name,
            Groups: groups,
            Host: Text("HostAdrs").Trim(),
            Port: Number("Port", 21),
            UserName: anonymous ? "" : Text("UserName").Trim(),
            StoredPassword: anonymous ? null : settings.GetString(key, "Password"),
            Anonymous: anonymous,
            LocalDirectory: Text("LocalDir").Trim(),
            RemoteDirectory: Text("RemoteDir").Trim(),
            Passive: Number("Pasv", 1) != 0,
            AllowPlain: Number("NoEncryption", 1) != 0,
            AllowExplicitTls: Number("FTPES", 1) != 0,
            AllowImplicitTls: Number("FTPIS", 1) != 0,
            FileNameKanji: Number("NameKanji", -1),
            MaxConnections: settings.GetInt(key, "ThreadCount") ?? settings.GetInt(DefaultHost, "ThreadCount"));
    }

    // 件数が書いていないとき（途中までの書き出しなど）は、Host0 から続いている数
    private static int CountHostKeys(FfftpSettings settings)
    {
        var count = 0;
        while (settings.HasKey($@"{Options}\Host{count}"))
            count++;
        return count;
    }
}
