using Hakobune.Core.Remote;
using Hakobune.Core.Remote.Ftp;
using Hakobune.Core.Remote.Sftp;

namespace Hakobune.Core.Tests.Integration;

/// <summary>実サーバーでのテストの接続先 1 つ。</summary>
/// <param name="Host">ホスト。</param>
/// <param name="Port">ポート。</param>
/// <param name="CreateFactory">ホストとポートを指定して、接続を作るものを返す（中継をはさむときに差し替える）。</param>
internal sealed record TestServer(string Host, int Port, Func<string, int, IRemoteSessionFactory> CreateFactory)
{
    public IRemoteSessionFactory Factory => CreateFactory(Host, Port);
}

/// <summary>
/// 実サーバーでのテストの接続先。環境変数 HAKOBUNE_TEST_&lt;名前&gt; に
/// 「ftp://ユーザー:パスワード@ホスト:ポート」の形で指定する。
/// ftpes:// は明示的 FTPS、ftps:// は暗黙的 FTPS、sftp:// は SFTP。
/// CI では tests/servers/start.sh で立てたサーバーを指す。指定が無ければテストは飛ばす。
/// </summary>
internal static class TestServers
{
    public static TestServer? Find(string name)
    {
        var value = Environment.GetEnvironmentVariable(VariableName(name));
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

        if (uri.Scheme == "sftp")
        {
            return new TestServer(uri.Host, uri.Port, (host, port) => new SftpSessionFactory(new SftpConnectionSettings
            {
                Host = host,
                Port = port,
                UserName = user,
                Password = password,
                ValidateHostKey = (_, _, _) => true, // テスト用のサーバーの鍵を受け入れる
                ConnectTimeout = TimeSpan.FromSeconds(15),
                OperationTimeout = TimeSpan.FromSeconds(30),
            }));
        }

        var security = uri.Scheme switch
        {
            "ftp" => FtpSecurity.None,
            "ftpes" => FtpSecurity.Explicit,
            "ftps" => FtpSecurity.Implicit,
            _ => throw new InvalidOperationException($"{VariableName(name)} の形式が正しくありません: {uri.Scheme}"),
        };
        return new TestServer(uri.Host, uri.Port, (host, port) => new FtpSessionFactory(new FtpConnectionSettings
        {
            Host = host,
            Port = port,
            UserName = user,
            Password = password,
            Security = security,
            ValidateCertificate = (_, _, _) => true, // テスト用の自己署名証明書を受け入れる
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ReadTimeout = TimeSpan.FromSeconds(30),
        }));
    }

    public static string SkipReason(string name) =>
        $"環境変数 {VariableName(name)} が無いため、実サーバーでのテストを飛ばします";

    /// <summary>ホームの下に、テストごとの新しいフォルダを作る。</summary>
    public static async Task<string> CreateTestDirectoryAsync(IRemoteSession session, CancellationToken cancellationToken)
    {
        var home = await session.GetHomeDirectoryAsync(cancellationToken);
        var directory = RemotePath.Combine(home, "hakobune-test-" + Guid.NewGuid().ToString("N")[..8]);
        await session.CreateDirectoryAsync(directory, cancellationToken);
        return directory;
    }

    public static byte[] RandomBytes(int length, int seed) => RandomBytes(length, new Random(seed));

    public static byte[] RandomBytes(int length, Random random)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static string VariableName(string name) => "HAKOBUNE_TEST_" + name;
}
