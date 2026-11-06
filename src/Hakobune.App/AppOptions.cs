using System.Globalization;
using Hakobune.Presentation.Demo;
using Microsoft.UI.Xaml;

namespace Hakobune.App;

/// <summary>
/// 起動のときの指定。ふだんは何も指定しない。画面写真を撮るときに使う。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>--connect URL</c>: 起動したら URL（例: sftp://user@example.com/var/www）に接続する（クイック接続。保存はしない）。</item>
/// <item><c>--demo</c>: 見本のデータで画面を出す。<c>--scenario stale</c> で、一覧の更新に失敗した場面にする。</item>
/// <item><c>--dialog 名前</c>: 見本のダイアログを出す（new、edit、password、hostkey、hostkey-changed、certificate、delete）。</item>
/// <item><c>--theme light|dark</c>: ライト・ダークを決める（省くと Windows の設定に合わせる）。</item>
/// <item><c>--screenshot ファイル名</c>: 画面写真を PNG で保存して終わる。</item>
/// <item><c>--size 幅x高さ</c>: ウインドウの中の大きさ（既定 1280x800）。</item>
/// </list>
/// </remarks>
internal sealed record AppOptions
{
    /// <summary>何も指定しないとき。</summary>
    public static AppOptions Default { get; } = new();

    /// <summary>見本の場面。null ならふつうに起動する。</summary>
    public DemoScenario? Demo { get; init; }

    /// <summary>見本のダイアログ。</summary>
    public DemoDialog? Dialog { get; init; }

    /// <summary>起動したら接続する URL。</summary>
    public string? ConnectUrl { get; init; }

    /// <summary>ライト・ダーク。null なら Windows の設定に合わせる。</summary>
    public ApplicationTheme? Theme { get; init; }

    /// <summary>画面写真の保存先。</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>ウインドウの中の幅。</summary>
    public int Width { get; init; } = 1280;

    /// <summary>ウインドウの中の高さ。</summary>
    public int Height { get; init; } = 800;

    /// <summary>コマンドラインを読む。知らない指定は無視する。</summary>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new AppOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--demo":
                    options = options with { Demo = options.Demo ?? DemoScenario.Normal };
                    break;
                case "--connect":
                    options = options with { ConnectUrl = string.IsNullOrWhiteSpace(value) ? null : value };
                    i++;
                    break;
                case "--dialog":
                    options = options with
                    {
                        Demo = options.Demo ?? DemoScenario.Normal,
                        Dialog = value?.ToLowerInvariant() switch
                        {
                            "new" => DemoDialog.NewConnection,
                            "edit" => DemoDialog.EditConnection,
                            "password" => DemoDialog.Password,
                            "hostkey" => DemoDialog.HostKey,
                            "hostkey-changed" => DemoDialog.ChangedHostKey,
                            "certificate" => DemoDialog.Certificate,
                            "delete" => DemoDialog.Delete,
                            _ => null,
                        },
                    };
                    i++;
                    break;
                case "--scenario":
                    options = options with
                    {
                        Demo = string.Equals(value, "stale", StringComparison.OrdinalIgnoreCase) ? DemoScenario.Stale : DemoScenario.Normal,
                    };
                    i++;
                    break;
                case "--theme":
                    options = options with
                    {
                        Theme = value?.ToLowerInvariant() switch
                        {
                            "dark" => ApplicationTheme.Dark,
                            "light" => ApplicationTheme.Light,
                            _ => null,
                        },
                    };
                    i++;
                    break;
                case "--screenshot":
                    options = options with { ScreenshotPath = value };
                    i++;
                    break;
                case "--size":
                    var parts = value?.Split('x', 'X') ?? [];
                    if (parts.Length == 2
                        && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
                        && width >= 320 && height >= 240)
                    {
                        options = options with { Width = width, Height = height };
                    }
                    i++;
                    break;
            }
        }
        return options;
    }
}
