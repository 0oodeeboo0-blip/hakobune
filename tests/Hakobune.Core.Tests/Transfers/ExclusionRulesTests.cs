using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Transfers;

/// <summary>送らない名前の決まり（Thumbs.db、*.tmp、.git/ など）。</summary>
public sealed class ExclusionRulesTests
{
    private static readonly ExclusionRules Defaults = new(["Thumbs.db", "desktop.ini", ".DS_Store", ".git/", "node_modules/", "~$*", "*.tmp"]);

    [Theory]
    [InlineData("Thumbs.db", false, "Thumbs.db")]
    [InlineData("images/products/THUMBS.DB", false, "Thumbs.db")]
    [InlineData(@"images\desktop.ini", false, "desktop.ini")]
    [InlineData("~$見積書.xlsx", false, "~$*")]
    [InlineData("js/cache.tmp", false, "*.tmp")]
    [InlineData(".git", true, ".git/")]
    [InlineData(".git/config", false, ".git/")]
    [InlineData("site/node_modules/react/index.js", false, "node_modules/")]
    [InlineData("index.html", false, null)]
    [InlineData("css", true, null)]
    [InlineData("notes.tmp.txt", false, null)]
    public void Names_folders_and_wildcards_match(string path, bool isDirectory, string? expected) =>
        Assert.Equal(expected, Defaults.Match(path, isDirectory));

    [Fact]
    public void A_folder_name_does_not_match_a_file_of_that_name()
    {
        var rules = new ExclusionRules(["backup/"]);

        Assert.Null(rules.Match("backup", isDirectory: false));
        Assert.Equal("backup/", rules.Match("backup", isDirectory: true));
        Assert.Equal("backup/", rules.Match(@"old\backup\index.html", isDirectory: false));
    }

    [Fact]
    public void A_name_without_a_slash_matches_files_and_folders()
    {
        var rules = new ExclusionRules(["backup"]);

        Assert.Equal("backup", rules.Match("backup", isDirectory: false));
        Assert.Equal("backup", rules.Match("backup/a.txt", isDirectory: false));
    }

    [Fact]
    public void Names_with_a_slash_count_from_the_folder_being_sent()
    {
        var rules = new ExclusionRules(["/config.php", "assets/cache/"]);

        Assert.Equal("/config.php", rules.Match("config.php", isDirectory: false));
        Assert.Null(rules.Match("admin/config.php", isDirectory: false));
        Assert.Equal("assets/cache/", rules.Match("assets/cache/a.png", isDirectory: false));
        Assert.Null(rules.Match("other/assets/cache/a.png", isDirectory: false));
        // 一覧の印は名前だけで見るので、位置で決まる名前は使わない
        Assert.Null(rules.MatchName("config.php", isDirectory: false));
    }

    [Fact]
    public void Match_last_looks_only_at_the_last_name()
    {
        Assert.Null(Defaults.MatchLast(".git/config", isDirectory: false));
        Assert.Equal(".git/", Defaults.MatchLast("site/.git", isDirectory: true));
        Assert.Equal("Thumbs.db", Defaults.MatchName("thumbs.db", isDirectory: false));
    }

    [Theory]
    [InlineData("", "名前を入れてください")]
    [InlineData("   ", "名前を入れてください")]
    [InlineData("/", "名前を入れてください")]
    [InlineData("*", "* だけの名前は、すべてに当てはまるので使えません")]
    [InlineData("*/", "* だけの名前は、すべてに当てはまるので使えません")]
    [InlineData("a//b", "/ が続いています")]
    [InlineData("*.psd", null)]
    [InlineData(@"backup\", null)]
    [InlineData("*/cache", null)]
    public void Names_are_checked_before_use(string pattern, string? expected) =>
        Assert.Equal(expected, ExclusionRules.Validate(pattern));

    [Fact]
    public void Bad_and_repeated_names_are_dropped()
    {
        var rules = new ExclusionRules(["*.psd", "*.PSD", " ", "*", @"backup\"]);

        Assert.Equal(new[] { "*.psd", "backup/" }, rules.Patterns.ToArray());
        Assert.True(ExclusionRules.None.IsEmpty);
        Assert.Null(ExclusionRules.None.Match("Thumbs.db", isDirectory: false));
    }
}
