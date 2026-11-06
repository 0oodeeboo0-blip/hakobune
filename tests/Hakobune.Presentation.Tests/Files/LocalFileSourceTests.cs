using Hakobune.Core.Transfers;
using Hakobune.Presentation.Files;

namespace Hakobune.Presentation.Tests.Files;

public sealed class LocalFileSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hakobune-tests", Guid.NewGuid().ToString("N"));

    public LocalFileSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Lists_files_and_folders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "サブフォルダ"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "日本語.txt"), new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);

        var listing = await new LocalFileSource().ListAsync(_root, TestContext.Current.CancellationToken);

        Assert.True(listing.Succeeded);
        var entries = listing.Entries.ToDictionary(entry => entry.Name);
        Assert.Equal(FileEntryType.Directory, entries["サブフォルダ"].Type);
        Assert.Equal(3, entries["日本語.txt"].Size);
        Assert.Equal(Path.Combine(_root, "日本語.txt"), entries["日本語.txt"].FullPath);
    }

    [Fact]
    public async Task A_missing_folder_is_reported_instead_of_thrown()
    {
        var listing = await new LocalFileSource().ListAsync(Path.Combine(_root, "none"), TestContext.Current.CancellationToken);

        Assert.False(listing.Succeeded);
        Assert.Equal(FailureReason.LocalFileNotFound, listing.Failure?.Reason);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void Parents_and_breadcrumbs_follow_the_path()
    {
        var source = new LocalFileSource();
        var nested = Path.Combine(_root, "a", "b");

        Assert.Equal(Path.Combine(_root, "a"), source.GetParent(nested));
        var root = Path.GetPathRoot(nested)!;
        Assert.Null(source.GetParent(root));

        var segments = source.GetSegments(nested);
        Assert.Equal(root, segments[0].Path);
        Assert.Equal(new[] { "a", "b" }, segments.TakeLast(2).Select(segment => segment.Label).ToArray());
        Assert.Equal(nested, segments[^1].Path);
    }
}
