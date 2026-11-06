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
    public void Permission_text_is_read(string text, string octal) =>
        Assert.Equal(Convert.ToInt32(octal, 8), UnixMode.FromText(text));

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
