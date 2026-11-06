using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;

namespace Hakobune.Presentation.Tests.Files;

public class FilePaneViewModelTests
{
    private static FakeFileSource SourceWithFiles()
    {
        var source = new FakeFileSource();
        source.Folders["/data"] =
        [
            FakeFileSource.File("/data", "b.txt", 300),
            FakeFileSource.Folder("/data", "sub"),
            FakeFileSource.File("/data", "a.txt", 5_000),
            FakeFileSource.Folder("/data", "Zed"),
        ];
        source.Folders["/data/sub"] = [FakeFileSource.File("/data/sub", "inner.txt", 1)];
        source.Folders["/"] = [FakeFileSource.Folder("/", "data")];
        return source;
    }

    private static async Task<(FilePaneViewModel Pane, FakeFileSource Source)> OpenAsync()
    {
        var source = SourceWithFiles();
        var pane = new FilePaneViewModel(PaneKind.Remote);
        await pane.SetSourceAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        return (pane, source);
    }

    private static string[] Names(FilePaneViewModel pane) => [.. pane.Items.Select(item => item.Name)];

    [Fact]
    public async Task Opens_the_start_folder_with_folders_first()
    {
        var (pane, _) = await OpenAsync();

        Assert.Equal("/data", pane.CurrentPath);
        Assert.Equal(new[] { "sub", "Zed", "a.txt", "b.txt" }, Names(pane));
        Assert.Equal(new[] { "server", "data" }, pane.Breadcrumbs.Select(segment => segment.Label).ToArray());
        Assert.Equal("4 項目", pane.SummaryText);
        Assert.False(pane.HasMessage);
        Assert.True(pane.CanGoUp);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_list_and_says_why()
    {
        var (pane, source) = await OpenAsync();

        source.Fault = _ => TransferFailure.Transient(FailureReason.ConnectionLost);
        await pane.RefreshAsync();

        // FFFTP のように一覧を消さない
        Assert.Equal(4, pane.Items.Count);
        Assert.True(pane.IsStale);
        Assert.True(pane.HasMessage);
        Assert.Equal("一覧を更新できませんでした", pane.MessageTitle);
        Assert.Equal("接続が切れました", pane.MessageReason);
        Assert.Equal("前回の一覧を表示しています（10秒後に再試行）", pane.MessageText);
        Assert.EndsWith("時点", pane.SummaryText, StringComparison.Ordinal);

        source.Fault = null;
        await pane.RefreshAsync();

        Assert.False(pane.IsStale);
        Assert.False(pane.HasMessage);
        Assert.Equal("4 項目", pane.SummaryText);
    }

    [Fact]
    public async Task A_permanent_refresh_failure_is_not_retried_automatically()
    {
        var (pane, source) = await OpenAsync();

        source.Fault = _ => FtpReplyClassifier.Classify(550, "Permission denied.");
        await pane.RefreshAsync();

        Assert.Equal(4, pane.Items.Count);
        Assert.Equal("権限がありません（550）", pane.MessageReason);
        Assert.Equal("前回の一覧を表示しています", pane.MessageText);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_opened_leaves_you_where_you_were()
    {
        var (pane, source) = await OpenAsync();
        source.Fault = path => path == "/data/sub" ? TransferFailure.Transient(FailureReason.Timeout) : null;

        await pane.OpenAsync(pane.Items.First(item => item.Name == "sub"));

        Assert.Equal("/data", pane.CurrentPath);
        Assert.Equal(4, pane.Items.Count);
        Assert.False(pane.IsStale);
        Assert.Equal("フォルダを開けませんでした", pane.MessageTitle);
        Assert.Equal("サーバーの応答がありません", pane.MessageReason);
        Assert.False(pane.HasMessageText);

        // 「再試行」で、開けなかったフォルダをもう一度開く
        source.Fault = null;
        await pane.RetryCommand.ExecuteAsync(null);

        Assert.Equal("/data/sub", pane.CurrentPath);
        Assert.Equal(new[] { "inner.txt" }, Names(pane));
        Assert.False(pane.HasMessage);
    }

    [Fact]
    public async Task Going_up_selects_the_folder_you_came_from()
    {
        var (pane, _) = await OpenAsync();
        await pane.NavigateAsync("/data/sub");

        await pane.GoUpCommand.ExecuteAsync(null);

        Assert.Equal("/data", pane.CurrentPath);
        Assert.Equal("sub", pane.FocusItem?.Name);
    }

    [Fact]
    public async Task The_filter_ignores_case_width_and_kana_type()
    {
        var source = new FakeFileSource();
        source.Folders["/data"] =
        [
            FakeFileSource.File("/data", "ファイル一覧.txt", 1),
            FakeFileSource.File("/data", "README.md", 1),
            FakeFileSource.File("/data", "other.txt", 1),
        ];
        var pane = new FilePaneViewModel(PaneKind.Remote);
        await pane.SetSourceAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        pane.FilterText = "ふぁいる";
        Assert.Equal(new[] { "ファイル一覧.txt" }, Names(pane));
        Assert.Equal("1 件が一致", pane.SummaryText);

        pane.FilterText = "ｒｅａｄ";
        Assert.Equal(new[] { "README.md" }, Names(pane));

        pane.FilterText = "見つからない";
        Assert.Empty(pane.Items);
        Assert.True(pane.ShowsEmptyText);
        Assert.Equal("一致する項目はありません", pane.EmptyText);
    }

    [Fact]
    public async Task Sorting_by_size_keeps_folders_on_top_and_toggles_the_direction()
    {
        var (pane, _) = await OpenAsync();

        pane.SortBy(FileSortColumn.Size);
        Assert.Equal(new[] { "sub", "Zed", "b.txt", "a.txt" }, Names(pane));

        pane.SortBy(FileSortColumn.Size);
        Assert.True(pane.SortDescending);
        Assert.Equal(new[] { "Zed", "sub", "a.txt", "b.txt" }, Names(pane));
    }

    [Fact]
    public async Task The_selection_count_is_shown_next_to_the_title()
    {
        var (pane, _) = await OpenAsync();

        pane.UpdateSelection(2);

        Assert.Equal("4 項目・2 個を選択", pane.SummaryText);
    }

    [Fact]
    public void A_server_pane_without_a_connection_says_what_to_do()
    {
        var pane = new FilePaneViewModel(PaneKind.Remote);

        Assert.False(pane.HasSource);
        Assert.True(pane.ShowsEmptyText);
        Assert.Equal("接続していません", pane.EmptyText);
        Assert.NotEqual("", pane.EmptyHint);
        Assert.Equal("", pane.SummaryText);
        Assert.False(pane.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_empty_folder_says_so()
    {
        var source = new FakeFileSource();
        source.Folders["/data"] = [];
        var pane = new FilePaneViewModel(PaneKind.Local);

        await pane.SetSourceAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(pane.ShowsEmptyText);
        Assert.Equal("このフォルダは空です", pane.EmptyText);
        Assert.Equal("0 項目", pane.SummaryText);
    }

    [Fact]
    public async Task The_start_folder_failing_is_reported_and_can_be_retried()
    {
        var source = SourceWithFiles();
        source.Fault = _ => TransferFailure.Transient(FailureReason.ConnectionLost);
        var pane = new FilePaneViewModel(PaneKind.Remote);

        await pane.SetSourceAsync(source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(pane.HasMessage);
        Assert.Equal("一覧を取得できませんでした", pane.MessageTitle);
        Assert.Equal("フォルダを開けませんでした", pane.EmptyText);

        source.Fault = null;
        await pane.RetryCommand.ExecuteAsync(null);

        Assert.Equal(4, pane.Items.Count);
        Assert.False(pane.HasMessage);
    }
}
