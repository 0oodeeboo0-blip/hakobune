using Hakobune.Core.Remote;

namespace Hakobune.Core.Tests.Remote;

public class RemotePathTests
{
    [Theory]
    [InlineData("/a/b/c.txt", "/a/b")]
    [InlineData("/a/b/", "/a")]
    [InlineData("/a", "/")]
    [InlineData("/", "/")]
    [InlineData("a/b", "a")]
    [InlineData("a", "")]
    public void GetParent_returns_the_parent_directory(string path, string expected)
    {
        Assert.Equal(expected, RemotePath.GetParent(path));
    }

    [Theory]
    [InlineData("/a/b/c.txt", "c.txt")]
    [InlineData("/a/b/", "b")]
    [InlineData("c.txt", "c.txt")]
    public void GetFileName_returns_the_last_name(string path, string expected)
    {
        Assert.Equal(expected, RemotePath.GetFileName(path));
    }

    [Theory]
    [InlineData("/a", "b", "/a/b")]
    [InlineData("/a/", "b", "/a/b")]
    [InlineData("/", "b", "/b")]
    [InlineData("", "b", "b")]
    public void Combine_joins_with_a_single_slash(string directory, string name, string expected)
    {
        Assert.Equal(expected, RemotePath.Combine(directory, name));
    }
}
