using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Hakobune.Core.Storage;

/// <summary>
/// 設定や送信待ちを JSON のファイルに保存する。いったん別の名前で書いてから置き換えるので、書いている途中で落ちても前の内容は壊れない。
/// </summary>
public static class JsonFile
{
    /// <summary>人が読める形（字下げあり、日本語はそのまま）で保存する設定。</summary>
    public static JsonSerializerOptions Readable { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
    };

    /// <summary>保存する。</summary>
    public static async Task SaveAsync<T>(string path, T value, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = PrepareDirectory(path);
        var temporary = TemporaryPath(fullPath);
        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, value, options ?? Readable, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>保存する（呼んだスレッドで待つ。小さなファイル向け）。</summary>
    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = PrepareDirectory(path);
        var temporary = TemporaryPath(fullPath);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, options ?? Readable);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// 読み込む。ファイルが無ければ null。壊れていれば「.broken」を付けて脇に置き、null を返す（アプリは空から始める）。
    /// </summary>
    public static async Task<T?> LoadAsync<T>(string path, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return null;

        try
        {
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
                return await JsonSerializer.DeserializeAsync<T>(stream, options ?? Readable, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            File.Move(fullPath, fullPath + ".broken", overwrite: true);
            return null;
        }
    }

    private static string PrepareDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        return fullPath;
    }

    // 同時に保存しても混ざらないよう、書きかけのファイル名は毎回変える
    private static string TemporaryPath(string fullPath) => $"{fullPath}.{Guid.NewGuid():N}.tmp";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
