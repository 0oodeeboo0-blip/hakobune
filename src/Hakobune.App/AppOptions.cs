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
/// <item><c>--upload パス</c>: <c>--connect</c> で接続したら、そのファイルかフォルダをサーバーへ送る（CI で転送を確かめる）。</item>
/// <item><c>--demo</c>: 見本のデータで画面を出す。<c>--scenario stale</c> で、一覧の更新に失敗した場面にする。
/// <c>--scenario refreshed</c> で、アップロードのあとに一覧を取り直した場面にする（増えた・変わった行が光っているところを撮る）。</item>
/// <item><c>--dialog 名前</c>: 見本のダイアログを出す（new、edit、quick、password、passphrase、hostkey、hostkey-changed、certificate、delete、
/// rename、delete-files、permissions、conflict、stop、settings、settings-exclusions）。</item>
/// <item><c>--theme light|dark</c>: ライト・ダークを決める（省くと Windows の設定に合わせる）。</item>
/// <item><c>--log</c>: 下の一覧で「ログ」を出しておく。</item>
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

    /// <summary>接続したら送るファイルかフォルダ。</summary>
    public string? UploadPath { get; init; }

    /// <summary>ライト・ダーク。null なら Windows の設定に合わせる。</summary>
    public ApplicationTheme? Theme { get; init; }

    /// <summary>画面写真の保存先。</summary>
    public string? ScreenshotPath { get; init; }

    /// <summary>下の一覧で「ログ」を出しておく。</summary>
    public bool ShowLog { get; init; }

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
                case "--upload":
                    options = options with { UploadPath = string.IsNullOrWhiteSpace(value) ? null : value };
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
                            "quick" => DemoDialog.QuickConnect,
                            "passphrase" => DemoDialog.Passphrase,
                            "password" => DemoDialog.Password,
                            "hostkey" => DemoDialog.HostKey,
                            "hostkey-changed" => DemoDialog.ChangedHostKey,
                            "certificate" => DemoDialog.Certificate,
                            "delete" => DemoDialog.Delete,
                            "rename" => DemoDialog.Rename,
                            "delete-files" => DemoDialog.DeleteFiles,
                            "permissions" => DemoDialog.Permissions,
                            "conflict" => DemoDialog.Conflict,
                            "stop" => DemoDialog.StopTransfers,
                            "settings" => DemoDialog.Settings,
                            "settings-exclusions" => DemoDialog.SettingsExclusions,
                            _ => null,
                        },
                    };
                    i++;
                    break;
                case "--scenario":
                    options = options with
                    {
                        Demo = value?.ToLowerInvariant() switch
                        {
                            "stale" => DemoScenario.Stale,
                            "refreshed" => DemoScenario.Refreshed,
                            _ => DemoScenario.Normal,
                        },
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
                case "--log":
                    options = options with { ShowLog = true };
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
