using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hakobune.Presentation.Updates;

/// <summary>GitHub の Releases に出ている版。</summary>
/// <param name="Version">版（例: 0.3.1）。</param>
/// <param name="Title">題（例: Hakobune 0.3.1）。</param>
/// <param name="Page">ダウンロードのページ。</param>
public sealed record UpdateInfo(Version Version, string Title, Uri Page);

/// <summary>新しい版を知らせるときに、GitHub の Releases からいちばん新しい版を読む。</summary>
public interface IUpdateSource
{
    /// <summary>いちばん新しい版の答え（JSON）。まだ 1 つも出ていなければ null。つながらなければ例外。</summary>
    Task<string?> GetLatestReleaseJsonAsync(CancellationToken cancellationToken);
}

/// <summary>新しい版が出ていないか、GitHub の Releases で確かめる。</summary>
public static class UpdateCheck
{
    /// <summary>いちばん新しい版を返す GitHub の API（下書きと先行版は返さない）。</summary>
    public static Uri LatestReleaseApi { get; } = new("https://api.github.com/repos/0oodeeboo0-blip/hakobune/releases/latest");

    /// <summary>Releases のページ。</summary>
    public static Uri ReleasesPage { get; } = new("https://github.com/0oodeeboo0-blip/hakobune/releases");

    /// <summary>確かめる間隔（起動のたびには確かめない）。</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(20);

    /// <summary>API の答え（JSON）を読む。下書き・先行版と、読めないものは null。</summary>
    public static UpdateInfo? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || IsTrue(root, "draft") || IsTrue(root, "prerelease")
                || ParseVersion(Text(root, "tag_name")) is not { } version
                || !Uri.TryCreate(Text(root, "html_url"), UriKind.Absolute, out var page)
                || page.Scheme != Uri.UriSchemeHttps)
                return null;
            var title = Text(root, "name") is { Length: > 0 } name ? name : $"Hakobune {Display(version)}";
            return new UpdateInfo(version, title, page);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>「v0.3.1」「0.3.1」「0.3.1+abc」などを、3 つの数の版にする。読めなければ null。</summary>
    public static Version? ParseVersion(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];
        var end = value.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
            value = value[..end];
        return Version.TryParse(value, out var version) ? Normalize(version) : null;
    }

    /// <summary>版を「0.3.1」の形にする。</summary>
    public static string Display(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
    }

    /// <summary><paramref name="latest"/> が今の版より新しければ返す。</summary>
    public static UpdateInfo? NewerThan(UpdateInfo? latest, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return latest is not null && Normalize(latest.Version) > Normalize(current) ? latest : null;
    }

    // 0.3 と 0.3.0 と 0.3.0.0 を同じに数える
    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>GitHub の API に聞く（送るのは、このアプリの名前と版だけ）。</summary>
public sealed class GitHubUpdateSource : IUpdateSource, IDisposable
{
    private readonly HttpClient _http;

    public GitHubUpdateSource(Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hakobune", UpdateCheck.Display(current)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <inheritdoc />
    public async Task<string?> GetLatestReleaseJsonAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(UpdateCheck.LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>新しい版を知らせるかと、前に確かめた結果（%APPDATA%\Hakobune\settings.json に残す）。</summary>
public sealed record UpdateSettings
{
    /// <summary>新しい版が出たら知らせる（起動したときに確かめる。間隔は <see cref="UpdateCheck.Interval"/>）。</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>前に確かめたとき。</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>前に確かめたときの、いちばん新しい版（例: 0.3.1）。</summary>
    public string? LatestVersion { get; init; }

    /// <summary>その版の題。</summary>
    public string? LatestTitle { get; init; }

    /// <summary>その版のページ。</summary>
    public string? LatestPage { get; init; }

    /// <summary>前に確かめた結果。</summary>
    public UpdateInfo? Latest() =>
        UpdateCheck.ParseVersion(LatestVersion) is { } version && Uri.TryCreate(LatestPage, UriKind.Absolute, out var page)
            ? new UpdateInfo(version, LatestTitle ?? $"Hakobune {UpdateCheck.Display(version)}", page)
            : null;

    /// <summary>確かめた結果を覚える。</summary>
    public UpdateSettings Remember(UpdateInfo? latest, DateTimeOffset checkedAt) => this with
    {
        LastCheckedAt = checkedAt,
        LatestVersion = latest is null ? null : UpdateCheck.Display(latest.Version),
        LatestTitle = latest?.Title,
        LatestPage = latest?.Page.ToString(),
    };

    /// <summary>そろそろ確かめ直すか。</summary>
    public bool IsDue(DateTimeOffset now) =>
        LastCheckedAt is not { } last || now - last >= UpdateCheck.Interval || now < last;
}
