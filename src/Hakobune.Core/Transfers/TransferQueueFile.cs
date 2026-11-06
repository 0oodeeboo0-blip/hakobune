using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hakobune.Core.Transfers;

/// <summary>
/// 保存しておく送信待ちの 1 件。アプリを閉じたり落ちたりしても、次に起動したとき続きから送れるようにする。
/// </summary>
public sealed record SavedTransferItem(
    Guid Id,
    TransferDirection Direction,
    string LocalPath,
    string RemotePath,
    ConflictPolicy OnConflict,
    TransferState State,
    int FailedAttempts,
    FailureReason? LastFailureReason,
    FailureKind? LastFailureKind,
    int? LastFailureCode,
    string? LastFailureMessage,
    bool HasWrittenTarget,
    long TotalBytes,
    long TransferredBytes,
    string? Note);

/// <summary>送信待ちをファイルに保存し、読み込む。</summary>
public static class TransferQueueFile
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 保存する。いったん別の名前で書いてから置き換えるので、書いている途中で落ちても前の内容は壊れない。
    /// </summary>
    public static async Task SaveAsync(string path, IReadOnlyList<SavedTransferItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(items);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = fullPath + ".tmp";
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, new QueueDocument(CurrentVersion, items), Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, fullPath, overwrite: true);
    }

    /// <summary>
    /// 読み込む。ファイルが無ければ空。壊れていれば「.broken」を付けて脇に置き、空から始める。
    /// </summary>
    public static async Task<IReadOnlyList<SavedTransferItem>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return [];

        try
        {
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                var document = await JsonSerializer.DeserializeAsync<QueueDocument>(stream, Options, cancellationToken).ConfigureAwait(false);
                return document?.Items ?? [];
            }
        }
        catch (JsonException)
        {
            File.Move(fullPath, fullPath + ".broken", overwrite: true);
            return [];
        }
    }

    internal sealed record QueueDocument(int Version, IReadOnlyList<SavedTransferItem> Items);
}
