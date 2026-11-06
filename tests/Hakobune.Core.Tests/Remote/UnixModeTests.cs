using Hakobune.Core.Remote;

namespace Hakobune.Core.Tests.Remote;

public class UnixModeTests
{
    [Theory]
    [InlineData("644", 644)]
    [InlineData("755", 755)]
    [InlineData("4755", 4755)]
    [InlineData("0", 0)]
    public void Modes_become_the_octal_digits_that_servers_expect(string octal, int digits) =>
        Assert.Equal(digits, UnixMode.ToOctalDigits(Convert.ToInt32(octal, 8)));

    [Theory]
    [InlineData("-rw-r--r--", "644")]
    [InlineData("drwxr-xr-x", "755")]
    [InlineData("rwx------", "700")]
    [InlineData("-rwsr-xr-x", "755")]
    [InlineData("0640", "640")]
    [InlineData("600", "600")]
    // SELinux の「.」、ACL の「+」、拡張属性の「@」が後ろに付くことがある
    [InlineData("-rw-r--r--.", "644")]
    [InlineData("drwxrwxr-x+", "775")]
    [InlineData("-rw-------@", "600")]
    public void Permission_text_is_read(string text, string octal) =>
        Assert.Equal(Convert.ToInt32(octal, 8), UnixMode.FromText(text));

    [Theory]
    [InlineData("644", RemoteItemType.File, "-rw-r--r--")]
    [InlineData("755", RemoteItemType.Directory, "drwxr-xr-x")]
    [InlineData("777", RemoteItemType.Link, "lrwxrwxrwx")]
    [InlineData("4755", RemoteItemType.File, "-rwsr-xr-x")]
    [InlineData("2750", RemoteItemType.Directory, "drwxr-s---")]
    [InlineData("1777", RemoteItemType.Directory, "drwxrwxrwt")]
    [InlineData("1666", RemoteItemType.Directory, "drw-rw-rwT")]
    public void Modes_are_shown_like_ls(string octal, RemoteItemType type, string text)
    {
        Assert.Equal(text, UnixMode.ToText(Convert.ToInt32(octal, 8), type));
        Assert.Equal(Convert.ToInt32(octal, 8) & 0x1FF, UnixMode.FromText(text));
    }

    [Theory]
    [InlineData(644, "644")]
    [InlineData(4755, "4755")]
    [InlineData(0, "0")]
    public void Octal_digits_from_servers_are_read_back(int digits, string octal) =>
        Assert.Equal(Convert.ToInt32(octal, 8), UnixMode.FromOctalDigits(digits));

    [Theory]
    [InlineData(-1)]
    [InlineData(648)]
    [InlineData(17777)]
    public void Numbers_that_are_not_octal_are_refused(int digits) => Assert.Null(UnixMode.FromOctalDigits(digits));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rw-r--r")]
    [InlineData("888")]
    [InlineData("adfrw")]
    public void Unknown_text_is_not_guessed(string? text) => Assert.Null(UnixMode.FromText(text));

    [Fact]
    public void Modes_are_written_with_three_digits() =>
        Assert.Equal("044", UnixMode.Format(Convert.ToInt32("44", 8)));
}
