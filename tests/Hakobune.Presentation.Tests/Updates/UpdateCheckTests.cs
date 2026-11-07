using Hakobune.Presentation.Connections;
using Hakobune.Presentation.Settings;
using Hakobune.Presentation.Tests.Connections;
using Hakobune.Presentation.Updates;

namespace Hakobune.Presentation.Tests.Updates;

/// <summary>新しい版のお知らせ（GitHub の Releases を見る）。</summary>
public sealed class UpdateCheckTests : IDisposable
{
    private const string Latest = """
        {"tag_name":"v0.3.1","name":"Hakobune 0.3.1","html_url":"https://github.com/0oodeeboo0-blip/hakobune/releases/tag/v0.3.1","draft":false,"prerelease":false}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero));

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

    [Fact]
    public void The_latest_release_is_read_from_the_github_answer()
    {
        var info = UpdateCheck.Parse(Latest);

        Assert.NotNull(info);
        Assert.Equal(new Version(0, 3, 1), info.Version);
        Assert.Equal("Hakobune 0.3.1", info.Title);
        Assert.Equal("https://github.com/0oodeeboo0-blip/hakobune/releases/tag/v0.3.1", info.Page.ToString());
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.4.0","html_url":"https://github.com/x","draft":true}""")]
    [InlineData("""{"tag_name":"v0.4.0-beta.1","html_url":"https://github.com/x","prerelease":true}""")]
    [InlineData("""{"tag_name":"latest","html_url":"https://github.com/x"}""")]
    [InlineData("""{"tag_name":"v0.4.0","html_url":"http://example.com/x"}""")]
    [InlineData("これは JSON ではない")]
    [InlineData("[]")]
    public void Drafts_prereleases_and_broken_answers_are_ignored(string json) => Assert.Null(UpdateCheck.Parse(json));

    [Theory]
    [InlineData("v0.3.1", "0.3.1")]
    [InlineData("0.3", "0.3.0")]
    [InlineData("0.3.0+abc1234", "0.3.0")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    public void Versions_are_read_with_three_numbers(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateCheck.ParseVersion(text));

    [Fact]
    public void Only_a_newer_version_is_announced()
    {
        var info = UpdateCheck.Parse(Latest)!;

        Assert.Same(info, UpdateCheck.NewerThan(info, new Version(0, 3, 0)));
        Assert.Null(UpdateCheck.NewerThan(info, new Version(0, 3, 1)));
        Assert.Null(UpdateCheck.NewerThan(info, new Version(0, 3, 1, 0)));
        Assert.Null(UpdateCheck.NewerThan(info, new Version(0, 4, 0)));
        Assert.Null(UpdateCheck.NewerThan(null, new Version(0, 3, 0)));
    }

    [Fact]
    public async Task A_newer_release_shows_a_link_and_is_checked_again_only_after_a_while()
    {
        var source = new ScriptedUpdateSource(Latest);
        using var main = await CreateAsync(source);

        await main.CheckForUpdatesAsync(new Version(0, 3, 0), TestContext.Current.CancellationToken);

        Assert.True(main.HasUpdate);
        Assert.Equal("新しい版 0.3.1 があります", main.UpdateText);
        Assert.Equal("https://github.com/0oodeeboo0-blip/hakobune/releases/tag/v0.3.1", main.UpdatePage?.ToString());
        Assert.Equal(1, source.Calls);
        // 確かめた結果は設定に残る（次の起動でも、間隔を空けるまでは聞かずに使う）
        var saved = await AppSettings.LoadAsync(Path.Combine(_root, "data", "settings.json"), TestContext.Current.CancellationToken);
        Assert.Equal("0.3.1", saved.Updates.LatestVersion);
        Assert.Equal(_time.GetUtcNow(), saved.Updates.LastCheckedAt);

        _time.Advance(TimeSpan.FromHours(1));
        await main.CheckForUpdatesAsync(new Version(0, 3, 0), TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Calls);
        Assert.True(main.HasUpdate);

        _time.Advance(UpdateCheck.Interval);
        source.Json = null; // まだ 1 つも出ていない（消された）
        await main.CheckForUpdatesAsync(new Version(0, 3, 0), TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Calls);
        Assert.False(main.HasUpdate);
    }

    [Fact]
    public async Task Nothing_is_shown_when_up_to_date_offline_or_turned_off()
    {
        var source = new ScriptedUpdateSource(Latest);
        using var main = await CreateAsync(source);

        // 今の版が新しい
        await main.CheckForUpdatesAsync(new Version(0, 3, 1), TestContext.Current.CancellationToken);
        Assert.False(main.HasUpdate);

        // つながらない
        _time.Advance(UpdateCheck.Interval);
        source.Failure = new HttpRequestException("つながりません");
        await main.CheckForUpdatesAsync(new Version(0, 3, 0), TestContext.Current.CancellationToken);
        Assert.False(main.HasUpdate);

        // 切ると確かめず、出ていた知らせも消す
        source.Failure = null;
        await main.CheckForUpdatesAsync(new Version(0, 3, 0), TestContext.Current.CancellationToken);
        Assert.True(main.HasUpdate);
        var calls = source.Calls;
        await main.ApplySettingsAsync(main.Settings with { Updates = main.Settings.Updates with { CheckForUpdates = false } });
        Assert.False(main.HasUpdate);
        _time.Advance(UpdateCheck.Interval);
        await main.CheckForUpdatesAsync(new Version(0, 3, 0), TestContext.Current.CancellationToken);
        Assert.Equal(calls, source.Calls);
        Assert.False(main.HasUpdate);
    }

    private async Task<MainViewModel> CreateAsync(IUpdateSource source)
    {
        var home = Path.Combine(_root, "site");
        Directory.CreateDirectory(home);
        var services = new AppServices(
            Path.Combine(_root, "data"), new MemorySecretStore(), new ScriptedPrompts(), new ScriptedDialogs(), Updates: source);
        return await MainViewModel.CreateAsync(services, _time, new AppSettings { LastLocalFolder = home });
    }

    private sealed class ScriptedUpdateSource(string? json) : IUpdateSource
    {
        public string? Json { get; set; } = json;

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public Task<string?> GetLatestReleaseJsonAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is { } failure ? Task.FromException<string?>(failure) : Task.FromResult(Json);
        }
    }
}
