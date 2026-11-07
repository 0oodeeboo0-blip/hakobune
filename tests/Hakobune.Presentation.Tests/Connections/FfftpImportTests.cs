using Hakobune.Core.Connections;
using Hakobune.Core.Import;
using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Demo;
using Hakobune.Presentation.Settings;

namespace Hakobune.Presentation.Tests.Connections;

/// <summary>FFFTP の接続先を読み込む画面と、読み込んだあと。</summary>
public sealed class FfftpImportTests : IDisposable
{
    // マスターパスワード「hakobune-master」で「hakobune-pass」を保存したもの
    private const string CustomMasterPassword = "0C000102030405060708090a0b0c0d0e0f:57002f455d023266a80c3b506acdbbb3cea1e601639be0638d4d44f20372f5c3";

    // CredentialSalt = 0x12345678 のときの、「hakobune-master」の確かめる値
    private const string CustomMasterCheck = "E@GK@NHOIEKHKAMGFDLOIKKCAKA@ELKLMO@N@ACM";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static FfftpHost Host(
        bool plain = true, bool explicitTls = true, bool implicitTls = true, int port = 21, int kanji = -1, int? connections = null, params string[] groups) =>
        new("ショップ", groups, "ftp.example.com", port, "web", null, false, @"C:\site", "/var/www", true, plain, explicitTls, implicitTls, kanji, connections);

    [Fact]
    public void Hosts_become_connections_with_the_closest_settings()
    {
        Assert.Equal(ConnectionProtocol.Auto, FfftpImportViewModel.ToProfile(Host()).Protocol);
        Assert.Equal(ConnectionProtocol.FtpsExplicit, FfftpImportViewModel.ToProfile(Host(plain: false, implicitTls: false)).Protocol);
        Assert.Equal(ConnectionProtocol.FtpsExplicit, FfftpImportViewModel.ToProfile(Host(plain: false)).Protocol);
        Assert.Equal(ConnectionProtocol.Ftp, FfftpImportViewModel.ToProfile(Host(explicitTls: false, implicitTls: false)).Protocol);
        var implicitTls = FfftpImportViewModel.ToProfile(Host(plain: false, explicitTls: false, port: 990));
        Assert.Equal(ConnectionProtocol.FtpsImplicit, implicitTls.Protocol);
        Assert.Equal(0, implicitTls.Port);

        var profile = FfftpImportViewModel.ToProfile(Host(port: 2121, kanji: 0, connections: 4, groups: ["仕事", "案件A"]));
        Assert.Equal("ショップ", profile.Name);
        Assert.Equal("仕事 / 案件A", profile.Group);
        Assert.Equal("ftp.example.com", profile.Host);
        Assert.Equal(2121, profile.Port);
        Assert.Equal("web", profile.UserName);
        Assert.Equal(@"C:\site", profile.LocalDirectory);
        Assert.Equal("/var/www", profile.RemoteDirectory);
        Assert.Equal("shift_jis", profile.FileNameEncoding);
        Assert.Equal(4, profile.MaxConcurrency);
        Assert.Equal(3, FfftpImportViewModel.ToProfile(Host()).MaxConcurrency);
        Assert.Equal("utf-8", FfftpImportViewModel.ToProfile(Host(kanji: 1)).FileNameEncoding);
    }

    [Fact]
    public void Connections_that_already_exist_are_not_chosen_at_first()
    {
        var existing = new ConnectionProfile { Name = "本番", Protocol = ConnectionProtocol.FtpsExplicit, Host = "EXAMPLE.com", UserName = "web" };
        var import = new FfftpImportViewModel([existing]);

        import.Load(DemoDialogs.SampleFfftpSettings(), "このパソコンの FFFTP の設定");

        Assert.Equal(new[] { "ショップ（本番）", "ショップ（検証）", "ブログ", "古いサーバー", "公開ミラー" }, import.Rows.Select(row => row.Name).ToArray());
        var shop = import.Rows[0];
        Assert.True(shop.IsExisting);
        Assert.False(shop.IsSelected);
        Assert.Equal("同じ接続先がもうあります", shop.NoteText);
        Assert.Equal("仕事", shop.GroupText);
        Assert.Equal(4, import.SelectedCount);
        Assert.Equal("4 件を読み込む", import.PrimaryText);
        Assert.Contains("JIS", import.Rows[3].NoteText, StringComparison.Ordinal);
        Assert.Equal("FTPS・example.com・web・パスワードあり", shop.DetailText);
        Assert.Contains("anonymous", import.Rows[4].DetailText, StringComparison.Ordinal);

        import.SelectNoneCommand.Execute(null);
        Assert.False(import.CanImport);
        import.SelectAllCommand.Execute(null);
        Assert.Equal(5, import.SelectedCount);
        Assert.Equal(5, import.BuildResult().Count);
        Assert.Equal("hakobune-pass", import.BuildResult()[0].Password);
    }

    [Fact]
    public void A_master_password_is_asked_before_passwords_are_read()
    {
        var settings = new FfftpSettings();
        settings.Set("FFFTP", "CredentialSalt", 0x12345678);
        settings.Set("FFFTP", "CredentialCheck", CustomMasterCheck);
        settings.Set(@"FFFTP\Options", "SetNum", 1);
        settings.Set(@"FFFTP\Options\Host0", "HostName", "ショップ");
        settings.Set(@"FFFTP\Options\Host0", "HostAdrs", "ftp.example.com");
        settings.Set(@"FFFTP\Options\Host0", "Password", CustomMasterPassword);
        var import = new FfftpImportViewModel([]);

        import.Load(settings, "ffftp.ini");

        Assert.True(import.NeedsMasterPassword);
        Assert.Null(import.Rows[0].Password);
        Assert.Contains("マスターパスワード", import.PasswordNote, StringComparison.Ordinal);

        import.MasterPassword = "wrong";
        import.CheckMasterPasswordCommand.Execute(null);
        Assert.True(import.NeedsMasterPassword);
        Assert.Equal("マスターパスワードが違います", import.MasterPasswordMessage);

        import.MasterPassword = "hakobune-master";
        import.CheckMasterPasswordCommand.Execute(null);
        Assert.False(import.NeedsMasterPassword);
        Assert.Equal("", import.MasterPasswordMessage);
        Assert.Equal("hakobune-pass", import.Rows[0].Password);
        Assert.True(import.ShowsPasswordNoteBelow);
    }

    [Fact]
    public async Task Imported_connections_are_saved_with_their_passwords()
    {
        var home = Path.Combine(_root, "site");
        Directory.CreateDirectory(home);
        var secrets = new MemorySecretStore();
        var dialogs = new ScriptedFfftpDialogs(DemoDialogs.SampleFfftpSettings());
        var services = new AppServices(Path.Combine(_root, "data"), secrets, new ScriptedPrompts(), new ScriptedDialogs(), FfftpImport: dialogs);
        using var main = await MainViewModel.CreateAsync(services, settings: new AppSettings { LastLocalFolder = home });
        Assert.True(main.CanImportFfftp);

        await main.ImportFfftpCommand.ExecuteAsync(null);

        var review = Assert.Single(dialogs.Reviews);
        Assert.Equal("このパソコンの FFFTP の設定", review.SourceText);
        Assert.Equal(5, main.Connections.Profiles.Count);
        var shop = main.Connections.Profiles.Single(profile => profile.Name == "ショップ（本番）");
        Assert.Equal("仕事", shop.Group);
        Assert.Equal("hakobune-pass", secrets.Read(shop.Id));
        Assert.Null(secrets.Read(main.Connections.Profiles.Single(profile => profile.Name == "公開ミラー").Id));
        Assert.Contains(main.Log.GetSnapshot().Entries, entry => entry.Message == "FFFTP から接続先を 5 件読み込みました（パスワードあり 3 件）");

        // 次に起動しても残っている
        using var again = await MainViewModel.CreateAsync(services, settings: new AppSettings { LastLocalFolder = home });
        Assert.Equal(5, again.Connections.Profiles.Count);
    }

    [Fact]
    public async Task Without_ffftp_on_this_computer_a_file_is_asked_for()
    {
        var home = Path.Combine(_root, "site");
        Directory.CreateDirectory(home);
        var ini = Path.Combine(_root, "ffftp.ini");
        await File.WriteAllTextAsync(
            ini, "[FFFTP]\r\nVersion=5700\r\n[FFFTP\\Options]\r\nSetNum=1\r\n[FFFTP\\Options\\Host0]\r\nHostName=blog\r\nHostAdrs=blog.example.com\r\n",
            TestContext.Current.CancellationToken);
        var dialogs = new ScriptedFfftpDialogs(null) { PickedFile = ini };
        var services = new AppServices(Path.Combine(_root, "data"), new MemorySecretStore(), new ScriptedPrompts(), new ScriptedDialogs(), FfftpImport: dialogs);
        using var main = await MainViewModel.CreateAsync(services, settings: new AppSettings { LastLocalFolder = home });

        await main.ImportFfftpCommand.ExecuteAsync(null);

        Assert.Equal("ffftp.ini", Assert.Single(dialogs.Reviews).SourceText);
        Assert.Equal("blog.example.com", Assert.Single(main.Connections.Profiles).Host);

        // ファイルも選ばなければ、何もしない
        dialogs.PickedFile = null;
        await main.ImportFfftpCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Reviews);
    }

    private sealed class ScriptedFfftpDialogs(FfftpSettings? installed) : IFfftpImportDialogs
    {
        public string? PickedFile { get; set; }

        public List<FfftpImportViewModel> Reviews { get; } = [];

        public FfftpSettings? ReadInstalledSettings() => installed;

        public Task<string?> PickSettingsFileAsync() => Task.FromResult(PickedFile);

        public Task<bool> ReviewAsync(FfftpImportViewModel import)
        {
            Reviews.Add(import);
            return Task.FromResult(true);
        }
    }
}
