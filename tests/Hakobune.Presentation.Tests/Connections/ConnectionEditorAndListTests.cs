using Hakobune.Core.Connections;
using Hakobune.Presentation.Connections;

namespace Hakobune.Presentation.Tests.Connections;

public class ConnectionEditorViewModelTests
{
    [Fact]
    public void Pasting_a_url_into_the_host_fills_the_other_fields()
    {
        var editor = new ConnectionEditorViewModel();

        editor.Host = "ftpes://web:secret@ftp.example.com:2121/public_html";

        Assert.Equal(ConnectionProtocol.FtpsExplicit, editor.Protocol);
        Assert.Equal("ftp.example.com", editor.Host);
        Assert.Equal("2121", editor.PortText);
        Assert.Equal("web", editor.UserName);
        Assert.Equal("secret", editor.Password);
        Assert.Equal("/public_html", editor.RemoteDirectory);
        Assert.True(editor.IsValid);
    }

    [Theory]
    [InlineData("", "", "web", "ホスト名を入れてください")]
    [InlineData("exa mple.com", "", "web", "ホスト名に空白は使えません")]
    [InlineData("example.com", "70000", "web", "ポート番号は 1〜65535 の数で入れてください")]
    [InlineData("example.com", "22a", "web", "ポート番号は 1〜65535 の数で入れてください")]
    [InlineData("example.com", "", "", "SFTP ではユーザー名が要ります")]
    [InlineData("example.com", "", "web", "")]
    [InlineData("example.com", "2222", "web", "")]
    public void Mistakes_are_explained(string host, string port, string userName, string message)
    {
        // 1 番目は SFTP（ユーザー名が要る方式）
        var editor = new ConnectionEditorViewModel { ProtocolIndex = 1, Host = host, PortText = port, UserName = userName };

        Assert.Equal(ConnectionProtocol.Sftp, editor.Protocol);
        Assert.Equal(message, editor.ValidationMessage);
        Assert.Equal(message.Length == 0, editor.IsValid);
    }

    [Fact]
    public void An_empty_host_only_disables_saving_without_scolding()
    {
        var editor = new ConnectionEditorViewModel();
        Assert.False(editor.IsValid);
        Assert.False(editor.ShowsValidationMessage);

        editor.Host = "example.com";
        editor.PortText = "0";

        Assert.True(editor.ShowsValidationMessage);
        Assert.Equal("example.com", editor.NamePlaceholder);
    }

    [Fact]
    public void A_new_connection_starts_with_auto()
    {
        var editor = new ConnectionEditorViewModel { Host = "ftp.example.com", UserName = "web" };

        Assert.Equal(ConnectionProtocol.Auto, editor.Protocol);
        Assert.True(editor.IsAuto);
        Assert.True(editor.IsFtp);
        Assert.False(editor.ShowsInsecureWarning);
        Assert.Equal("21", editor.PortPlaceholder);
        Assert.True(editor.IsValid);
        Assert.Equal(ConnectionProtocol.Auto, editor.ToProfile().Protocol);
    }

    [Fact]
    public void An_existing_connection_keeps_its_protocol()
    {
        var editor = new ConnectionEditorViewModel(new ConnectionProfile { Protocol = ConnectionProtocol.FtpsExplicit, Host = "example.com", UserName = "web" });

        Assert.Equal(ConnectionProtocol.FtpsExplicit, editor.Protocol);
        Assert.False(editor.IsAuto);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Anonymous_ftp_needs_no_user_name(int protocolIndex)
    {
        // 0 番目は自動、4 番目は暗号化なしの FTP
        var editor = new ConnectionEditorViewModel { ProtocolIndex = protocolIndex, Host = "ftp.example.com" };

        Assert.True(editor.IsFtp);
        Assert.True(editor.IsValid);
        Assert.Equal("空欄なら anonymous（匿名）で入ります", editor.UserNamePlaceholder);
    }

    [Fact]
    public void A_choice_not_to_save_the_password_is_kept_when_editing()
    {
        var profile = new ConnectionProfile { Name = "ブログ", Host = "blog.example.com", UserName = "me" };

        Assert.True(new ConnectionEditorViewModel().SavePassword);
        Assert.False(new ConnectionEditorViewModel(profile, hasSavedPassword: false).SavePassword);
        Assert.True(new ConnectionEditorViewModel(profile, hasSavedPassword: true).SavePassword);
    }

    [Fact]
    public void Quick_connect_saves_nothing()
    {
        var editor = new ConnectionEditorViewModel(quickConnect: true) { Host = "sftp://web:secret@example.com/" };

        var result = editor.ToResult();

        Assert.Equal("クイック接続", editor.Title);
        Assert.Equal("接続", editor.PrimaryButtonText);
        Assert.False(editor.ShowsSaveOptions);
        Assert.False(result.SavePassword);
        Assert.Equal("secret", result.Password);
    }

    [Fact]
    public void Advanced_settings_are_open_when_something_was_changed()
    {
        Assert.False(new ConnectionEditorViewModel().OpensAdvancedSettings);
        Assert.True(new ConnectionEditorViewModel(new ConnectionProfile { Host = "a", RemoteDirectory = "/www" }).OpensAdvancedSettings);
        Assert.True(new ConnectionEditorViewModel(new ConnectionProfile { Host = "a", FileNameEncoding = "shift_jis" }).OpensAdvancedSettings);
    }

    [Fact]
    public void Editing_keeps_the_id_and_an_empty_password_keeps_the_saved_one()
    {
        var original = new ConnectionProfile { Name = "ブログ", Protocol = ConnectionProtocol.Ftp, Host = "blog.example.com", UserName = "me", FileNameEncoding = "shift_jis" };
        var editor = new ConnectionEditorViewModel(original, hasSavedPassword: true);

        Assert.False(editor.IsNew);
        Assert.True(editor.ShowsInsecureWarning);
        Assert.True(editor.IsFtp);
        Assert.Equal(1, editor.EncodingIndex);
        Assert.Equal("保存してあるパスワードを使う", editor.PasswordPlaceholder);

        editor.Name = "  ブログ（新）  ";
        var result = editor.ToResult();

        Assert.Equal(original.Id, result.Profile.Id);
        Assert.Equal("ブログ（新）", result.Profile.Name);
        Assert.Equal("shift_jis", result.Profile.FileNameEncoding);
        Assert.Null(result.Password);
    }

    [Fact]
    public void Each_connection_can_add_names_not_to_send()
    {
        var editor = new ConnectionEditorViewModel { Host = "example.com", UserName = "web", ExcludePatterns = "config.php\r /cache/ \r\r" };

        Assert.True(editor.IsValid);
        Assert.Equal("config.php\n/cache/", editor.ToProfile().ExcludePatterns);
        Assert.Equal(new[] { "config.php", "/cache/" }, editor.ToProfile().ExcludePatternList.ToArray());

        editor.ExcludePatterns = "*";
        Assert.Equal("送らない名前「*」: * だけの名前は、すべてに当てはまるので使えません", editor.ValidationMessage);

        var reopened = new ConnectionEditorViewModel(new ConnectionProfile { Host = "example.com", ExcludePatterns = "a.txt\nb/" });
        Assert.True(reopened.OpensAdvancedSettings);
        Assert.Equal("a.txt" + Environment.NewLine + "b/", reopened.ExcludePatterns);
    }

    [Fact]
    public void Switching_protocols_changes_the_default_port_and_options()
    {
        var editor = new ConnectionEditorViewModel();
        Assert.Equal("21", editor.PortPlaceholder);

        editor.ProtocolIndex = 1;
        editor.PrivateKeyPath = @"C:\keys\id_ed25519";

        Assert.Equal(ConnectionProtocol.Sftp, editor.Protocol);
        Assert.Equal("22", editor.PortPlaceholder);
        Assert.True(editor.IsSftp);
        Assert.False(editor.IsAuto);
        Assert.Equal("", editor.UserNamePlaceholder);
        Assert.Equal(@"C:\keys\id_ed25519", editor.ToProfile().PrivateKeyPath);

        editor.ProtocolIndex = 3;

        Assert.Equal(ConnectionProtocol.FtpsImplicit, editor.Protocol);
        Assert.Equal("990", editor.PortPlaceholder);
        Assert.True(editor.IsFtp);
        Assert.Null(editor.ToProfile().PrivateKeyPath);
    }
}

public class ConnectionListViewModelTests
{
    [Fact]
    public void Connections_are_grouped_in_the_order_they_first_appear()
    {
        var list = new ConnectionListViewModel(
        [
            new ConnectionProfile { Name = "A", Group = "仕事", Host = "a" },
            new ConnectionProfile { Name = "B", Group = "個人", Host = "b" },
            new ConnectionProfile { Name = "C", Group = "仕事", Host = "c" },
            new ConnectionProfile { Name = "D", Host = "d" },
        ]);

        Assert.Equal(new[] { "仕事", "個人", ConnectionListViewModel.DefaultGroupName }, list.Groups.Select(group => group.Name).ToArray());
        Assert.Equal(new[] { "A", "C" }, list.Groups[0].Items.Select(item => item.Name).ToArray());
        Assert.False(list.IsEmpty);
    }

    [Fact]
    public void Adding_changing_and_removing_are_announced_for_saving()
    {
        var list = new ConnectionListViewModel([]);
        var changes = 0;
        list.Changed += (_, _) => changes++;
        Assert.True(list.IsEmpty);

        var item = list.Add(new ConnectionProfile { Name = "A", Host = "a" });
        list.Groups[0].IsExpanded = false;
        list.Update(item, item.Profile with { Group = "仕事", Name = "A2" });
        list.Select(item);
        list.Remove(item);

        Assert.Equal(3, changes);
        Assert.True(list.IsEmpty);
        Assert.Null(list.Selected);
        Assert.Empty(list.Groups);
    }

    [Fact]
    public void Quick_connections_are_listed_apart_and_not_saved()
    {
        var list = new ConnectionListViewModel([new ConnectionProfile { Name = "A", Host = "a" }]);
        var changes = 0;
        list.Changed += (_, _) => changes++;

        var quick = list.AddTemporary(new ConnectionProfile { Host = "Quick.example.com", UserName = "web" });
        var again = list.AddTemporary(new ConnectionProfile { Host = "quick.example.com", UserName = "web" });

        Assert.Same(quick, again);
        Assert.True(quick.IsTemporary);
        Assert.Equal(0, changes);
        Assert.Equal(new[] { ConnectionListViewModel.DefaultGroupName, ConnectionListViewModel.TemporaryGroupName }, list.Groups.Select(group => group.Name).ToArray());
        Assert.Equal(new[] { "A" }, list.Profiles.Select(profile => profile.Name).ToArray());

        // 編集して保存すると、ふつうの接続先になる
        list.Update(quick, quick.Profile with { Name = "Q", Group = "仕事" });

        Assert.False(quick.IsTemporary);
        Assert.Equal(1, changes);
        Assert.Equal(new[] { "A", "Q" }, list.Profiles.Select(profile => profile.Name).ToArray());
        Assert.DoesNotContain(list.Groups, group => group.Name == ConnectionListViewModel.TemporaryGroupName);
    }

    [Fact]
    public void Updating_keeps_the_id_and_the_open_state_of_groups()
    {
        var list = new ConnectionListViewModel([new ConnectionProfile { Name = "A", Group = "仕事", Host = "a" }, new ConnectionProfile { Name = "B", Group = "個人", Host = "b" }]);
        var item = list.Items[1];
        var id = item.Profile.Id;
        list.Groups[0].IsExpanded = false;

        list.Update(item, new ConnectionProfile { Name = "B2", Group = "趣味", Host = "b" });

        Assert.Equal(id, item.Profile.Id);
        Assert.Equal("B2", item.Name);
        Assert.False(list.Groups.Single(group => group.Name == "仕事").IsExpanded);
        Assert.Contains(list.Groups, group => group.Name == "趣味");
        Assert.Same(item, list.Find(id));
    }
}
