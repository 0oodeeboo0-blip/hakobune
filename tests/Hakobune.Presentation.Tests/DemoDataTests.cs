using Hakobune.Presentation.Demo;

namespace Hakobune.Presentation.Tests;

/// <summary>見本の画面が、デザイン案と同じ数字になっているか。</summary>
public class DemoDataTests
{
    [Fact]
    public async Task The_transfer_list_matches_the_design()
    {
        using var main = await DemoData.CreateAsync(DemoScenario.Normal);
        var transfers = main.Transfers;

        Assert.Equal(new[] { "500", "3", "118", "377", "2" }, transfers.Tabs.Select(tab => tab.CountText).ToArray());
        Assert.Equal("完了 377・失敗 2", transfers.SummaryText);
        Assert.Equal("76%", transfers.OverallPercentText);
        Assert.Equal("379 / 500 件", transfers.CountText);
        Assert.Equal("6.3 MB/s", transfers.SpeedText);
        Assert.Equal("残り 約4分", transfers.RemainingText);
        Assert.Equal("同時転送 3", transfers.ConcurrencyText);

        var rows = transfers.Rows.Take(5).ToList();
        Assert.Equal("winter-coat-01.jpg", rows[0].Name);
        Assert.Equal("62%", rows[0].PercentText);
        Assert.Equal("3.4 MB/s・残り 2秒", rows[0].Detail);
        Assert.Equal("→ /var/www/html/商品写真", rows[0].Destination);
        Assert.Equal("winter-coat-02.jpg", rows[1].Name);
        Assert.Equal("2.9 MB/s・残り 4秒", rows[1].Detail);
        Assert.Equal("scarf-red-05.jpg", rows[2].Name);
        Assert.Equal("接続が切れたため・2/5回目・8秒後に再開", rows[2].Detail);
        Assert.Equal("サーバーの容量が足りません（552）", rows[3].Detail);
        Assert.Equal("権限がありません（550）", rows[4].Detail);
    }

    [Fact]
    public async Task The_file_lists_match_the_design()
    {
        using var main = await DemoData.CreateAsync(DemoScenario.Normal);

        Assert.Equal(
            new[] { "css", "images", "js", "商品写真", ".htaccess", "about.html", "backup-2026-11.zip", "index.html", "logo.png", "robots.txt", "sitemap.xml", "お問い合わせ.html" },
            main.Local.Items.Select(item => item.Name).ToArray());
        Assert.Equal(
            new[] { "", "", "", "", "2 KB", "9 KB", "128.4 MB", "18 KB", "46 KB", "1 KB", "4 KB", "12 KB" },
            main.Local.Items.Select(item => item.SizeText).ToArray());
        Assert.Equal("2026/11/05 18:22", main.Local.Items[0].ModifiedText);
        Assert.Equal("商品写真", main.Local.FocusItem?.Name);
        Assert.Equal(new[] { "C:", "Users", "user", "Sites", "shop" }, main.Local.Breadcrumbs.Select(segment => segment.Label).ToArray());

        Assert.Equal(11, main.Remote.Items.Count);
        Assert.Equal("drwxr-xr-x", main.Remote.Items[0].Permissions);
        Assert.Equal(new[] { "example.com", "var", "www", "html" }, main.Remote.Breadcrumbs.Select(segment => segment.Label).ToArray());
        Assert.Equal("11 項目", main.Remote.SummaryText);
        Assert.False(main.Remote.HasMessage);
    }

    [Fact]
    public async Task The_stale_scenario_keeps_the_previous_server_list()
    {
        using var main = await DemoData.CreateAsync(DemoScenario.Stale);

        Assert.True(main.Remote.IsStale);
        Assert.Equal(11, main.Remote.Items.Count);
        Assert.Equal("11 項目・12:36 時点", main.Remote.SummaryText);
        Assert.Equal("一覧を更新できませんでした", main.Remote.MessageTitle);
        Assert.Equal("接続が切れました", main.Remote.MessageReason);
        Assert.Equal("前回の一覧を表示しています（10秒後に再試行）", main.Remote.MessageText);
    }

    [Fact]
    public async Task The_connections_and_title_bar_match_the_design()
    {
        using var main = await DemoData.CreateAsync(DemoScenario.Normal);

        Assert.Equal(new[] { "仕事", "個人" }, main.Connections.Groups.Select(group => group.Name).ToArray());
        Assert.Equal("ショップ（本番）", main.Connections.Selected?.Name);
        Assert.True(main.Connections.Groups[1].Items[0].IsInsecure);
        Assert.Equal("SFTP・暗号化", main.Connection.SecurityText);
        Assert.Equal("example.com", main.Connection.Host);
    }
}
