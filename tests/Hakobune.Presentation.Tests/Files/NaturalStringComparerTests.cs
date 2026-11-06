using Hakobune.Presentation.Files;

namespace Hakobune.Presentation.Tests.Files;

public class NaturalStringComparerTests
{
    private static string[] Sort(params string[] names) => [.. names.Order(NaturalStringComparer.Instance)];

    [Fact]
    public void Numbers_inside_names_are_compared_as_numbers()
    {
        Assert.Equal(
            new[] { "file1.txt", "file2.txt", "File3.txt", "file10.txt", "file100.txt" },
            Sort("file10.txt", "file2.txt", "file100.txt", "file1.txt", "File3.txt"));
    }

    [Fact]
    public void Leading_zeros_and_very_long_numbers_are_handled()
    {
        Assert.Equal(new[] { "img007.png", "img7.png", "img8.png" }, Sort("img8.png", "img7.png", "img007.png"));
        Assert.Equal(new[] { "a2", "a12345678901234567890" }, Sort("a12345678901234567890", "a2"));
    }

    [Fact]
    public void Names_sort_like_explorer_in_japanese()
    {
        Assert.Equal(
            new[] { ".htaccess", "about.html", "backup-2026-11.zip", "index.html", "logo.png", "robots.txt", "sitemap.xml", "お問い合わせ.html" },
            Sort("お問い合わせ.html", "index.html", ".htaccess", "about.html", "sitemap.xml", "robots.txt", "logo.png", "backup-2026-11.zip"));
    }

    [Fact]
    public void Kana_type_is_ignored_but_the_order_stays_the_same_every_time()
    {
        Assert.Equal(new[] { "a", "b", "あ", "ア" }, Sort("ア", "b", "あ", "a"));
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("same", "same"));
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
    }
}
