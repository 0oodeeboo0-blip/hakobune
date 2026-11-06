using Hakobune.Core.Connections;
using Hakobune.Core.Remote.Ftp;
using Hakobune.Core.Remote.Sftp;
using Hakobune.Core.Tests.Fakes;

namespace Hakobune.Core.Tests.Connections;

public class ConnectionProfileTests
{
    [Theory]
    [InlineData("sftp://user@example.com/var/www", ConnectionProtocol.Sftp, "example.com", 0, "user", null, "/var/www")]
    [InlineData("sftp://user:p%40ss@example.com:2222", ConnectionProtocol.Sftp, "example.com", 2222, "user", "p@ss", "")]
    [InlineData("ftp://anonymous@ftp.example.jp:21/", ConnectionProtocol.Ftp, "ftp.example.jp", 0, "anonymous", null, "")]
    [InlineData("ftpes://web:secret@192.168.0.10/public_html", ConnectionProtocol.FtpsExplicit, "192.168.0.10", 0, "web", "secret", "/public_html")]
    [InlineData("ftps://host.example.com:990", ConnectionProtocol.FtpsImplicit, "host.example.com", 0, "", null, "")]
    public void Urls_become_connections(string url, ConnectionProtocol protocol, string host, int port, string user, string? password, string path)
    {
        Assert.True(ConnectionProfile.TryParseUrl(url, out var profile, out var parsedPassword));

        Assert.Equal(protocol, profile.Protocol);
        Assert.Equal(host, profile.Host);
        Assert.Equal(port, profile.Port);
        Assert.Equal(user, profile.UserName);
        Assert.Equal(password, parsedPassword);
        Assert.Equal(path, profile.RemoteDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("http://example.com")]
    [InlineData("sftp://")]
    public void Other_text_is_not_a_connection(string text)
    {
        Assert.False(ConnectionProfile.TryParseUrl(text, out _, out _));
    }

    [Fact]
    public void Ports_default_by_protocol()
    {
        Assert.Equal(22, new ConnectionProfile { Protocol = ConnectionProtocol.Sftp }.EffectivePort);
        Assert.Equal(21, new ConnectionProfile { Protocol = ConnectionProtocol.FtpsExplicit }.EffectivePort);
        Assert.Equal(990, new ConnectionProfile { Protocol = ConnectionProtocol.FtpsImplicit }.EffectivePort);
        Assert.Equal(2121, new ConnectionProfile { Protocol = ConnectionProtocol.Ftp, Port = 2121 }.EffectivePort);
        Assert.Equal("SFTP・example.com", new ConnectionProfile { Host = "example.com" }.Describe());
        Assert.Equal("FTP・example.com:2121", new ConnectionProfile { Protocol = ConnectionProtocol.Ftp, Host = "example.com", Port = 2121 }.Describe());
    }

    [Fact]
    public async Task Connections_are_saved_without_passwords_and_read_back()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "connections.json");
        var profiles = new[]
        {
            new ConnectionProfile { Name = "ショップ（本番）", Group = "仕事", Host = "example.com", UserName = "web", RemoteDirectory = "/var/www/html" },
            new ConnectionProfile { Name = "ブログ", Group = "個人", Protocol = ConnectionProtocol.Ftp, Host = "blog.example.com", FileNameEncoding = "shift_jis" },
        };

        await ConnectionStore.SaveAsync(file, profiles, TestContext.Current.CancellationToken);
        var loaded = await ConnectionStore.LoadAsync(file, TestContext.Current.CancellationToken);

        Assert.Equal(profiles, loaded);
        var json = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        Assert.Contains("ショップ（本番）", json, StringComparison.Ordinal); // 日本語はそのまま読める
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EffectivePort", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_connection_file_means_no_connections()
    {
        using var temp = new TempDirectory();

        Assert.Empty(await ConnectionStore.LoadAsync(Path.Combine(temp.Path, "none.json"), TestContext.Current.CancellationToken));
    }
}

public class TrustStoreTests
{
    [Fact]
    public void A_new_server_is_unknown_until_trusted_and_a_changed_key_is_noticed()
    {
        var store = TrustStore.InMemory();

        Assert.Equal(TrustState.Unknown, store.Check("Example.com", 22, "ssh-ed25519", "AAAA").State);

        store.Trust("Example.com", 22, "ssh-ed25519", "AAAA");
        Assert.Equal(TrustState.Trusted, store.Check("example.com", 22, "ssh-ed25519", "AAAA").State);

        var (state, previous) = store.Check("example.com", 22, "ssh-ed25519", "BBBB");
        Assert.Equal(TrustState.Changed, state);
        Assert.Equal("AAAA", previous?.Fingerprint);

        // ポートが違えば別の接続先
        Assert.Equal(TrustState.Unknown, store.Check("example.com", 2222, "ssh-ed25519", "AAAA").State);
    }

    [Fact]
    public async Task Trusted_keys_survive_a_restart()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "trusted-keys.json");
        var first = await TrustStore.LoadAsync(file, cancellationToken: TestContext.Current.CancellationToken);

        first.Trust("example.com", 22, "ssh-ed25519", "AAAA");
        first.Trust("example.com", 22, "ssh-ed25519", "CCCC"); // 置き換える
        first.Trust("ftp.example.com", 21, "x509", "0123ABCD");
        var second = await TrustStore.LoadAsync(file, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, second.Keys.Count);
        Assert.Equal(TrustState.Trusted, second.Check("example.com", 22, "ssh-ed25519", "CCCC").State);
        Assert.Equal(TrustState.Trusted, second.Check("ftp.example.com", 21, "x509", "0123ABCD").State);

        second.Forget("example.com", 22);
        var third = await TrustStore.LoadAsync(file, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(third.Keys);
    }
}

public class SessionFactoriesTests
{
    private static bool AcceptAll(string algorithm, string fingerprint, int keyLength) => true;

    [Fact]
    public void Sftp_profiles_make_sftp_sessions_with_the_host_key_check()
    {
        var profile = new ConnectionProfile { Protocol = ConnectionProtocol.Sftp, Host = " example.com ", Port = 2222, UserName = "web" };

        var factory = Assert.IsType<SftpSessionFactory>(SessionFactories.Create(profile, new ConnectionCredentials("secret"), AcceptAll, null));

        Assert.Equal("example.com", factory.Settings.Host);
        Assert.Equal(2222, factory.Settings.Port);
        Assert.Equal("web", factory.Settings.UserName);
        Assert.Equal("secret", factory.Settings.Password);
        Assert.NotNull(factory.Settings.ValidateHostKey);
        Assert.Null(factory.Settings.FileNameEncoding);
    }

    [Theory]
    [InlineData(ConnectionProtocol.Ftp, FtpSecurity.None)]
    [InlineData(ConnectionProtocol.FtpsExplicit, FtpSecurity.Explicit)]
    [InlineData(ConnectionProtocol.FtpsImplicit, FtpSecurity.Implicit)]
    public void Ftp_profiles_choose_the_encryption(ConnectionProtocol protocol, FtpSecurity security)
    {
        var profile = new ConnectionProfile { Protocol = protocol, Host = "ftp.example.com", UserName = "web", FileNameEncoding = "shift_jis", UsePassiveMode = false };

        var factory = Assert.IsType<FtpSessionFactory>(SessionFactories.Create(profile, new ConnectionCredentials("secret"), AcceptAll, null));

        Assert.Equal(security, factory.Settings.Security);
        Assert.Equal(932, factory.Settings.FileNameEncoding?.CodePage);
        Assert.False(factory.Settings.UsePassiveMode);
    }

    [Fact]
    public void An_empty_ftp_user_name_logs_in_anonymously()
    {
        var profile = new ConnectionProfile { Protocol = ConnectionProtocol.Ftp, Host = "ftp.example.jp" };

        var factory = Assert.IsType<FtpSessionFactory>(SessionFactories.Create(profile, new ConnectionCredentials(null), AcceptAll, null));

        Assert.Equal("anonymous", factory.Settings.UserName);
        Assert.False(SessionFactories.NeedsPassword(profile));
        Assert.True(SessionFactories.NeedsPassword(profile with { UserName = "web" }));
        Assert.False(SessionFactories.NeedsPassword(new ConnectionProfile { Host = "h", PrivateKeyPath = "id_ed25519" }));
    }

    [Theory]
    [InlineData("utf-8", null)]
    [InlineData("", null)]
    [InlineData("Shift_JIS", 932)]
    [InlineData("euc-jp", 51932)]
    [InlineData("no-such-encoding", null)]
    public void Encodings_are_found_by_name(string name, int? codePage)
    {
        Assert.Equal(codePage, SessionFactories.GetEncoding(name)?.CodePage);
    }
}
