namespace Hakobune.App;

/// <summary>思いがけないエラーを記録する。画面写真を撮っているときは、写真の隣に書く（CI で読むため）。</summary>
internal static class CrashReport
{
    public static void Write(Exception exception)
    {
        try
        {
            var path = App.Options.ScreenshotPath is { } screenshot
                ? screenshot + ".error.txt"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hakobune", "crash.log");
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 記録できなくても、元のエラーの処理を続ける
        }
    }
}
