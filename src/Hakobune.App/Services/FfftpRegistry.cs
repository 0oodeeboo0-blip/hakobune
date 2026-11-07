using Hakobune.Core.Import;
using Microsoft.Win32;

namespace Hakobune.App.Services;

/// <summary>このパソコンの FFFTP の設定（HKEY_CURRENT_USER\Software\Sota\FFFTP）を読む。</summary>
internal static class FfftpRegistry
{
    private const string RootPath = @"Software\Sota\FFFTP";
    private const int MaxDepth = 4;

    /// <summary>読む。FFFTP を使っていなければ（キーが無ければ）null。</summary>
    public static FfftpSettings? Read()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RootPath);
            if (root is null)
                return null;
            var settings = new FfftpSettings();
            ReadKey(root, "FFFTP", settings, 0);
            return settings;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static void ReadKey(RegistryKey key, string path, FfftpSettings settings, int depth)
    {
        foreach (var name in key.GetValueNames())
        {
            switch (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames))
            {
                case string text:
                    settings.Set(path, name, text);
                    break;
                case int number:
                    settings.Set(path, name, number);
                    break;
                case byte[] bytes:
                    settings.Set(path, name, bytes);
                    break;
            }
        }
        if (depth >= MaxDepth)
            return;
        foreach (var child in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(child);
            if (subKey is not null)
                ReadKey(subKey, $@"{path}\{child}", settings, depth + 1);
        }
    }
}
