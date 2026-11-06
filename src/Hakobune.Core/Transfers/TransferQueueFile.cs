using System.Text.Json;
using System.Text.Json.Serialization;
using Hakobune.Core.Storage;

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
    string? Note,
    bool SizeVerified = false,
    DateTimeOffset? FinishedAt = null,
    bool IsFolder = false,
    bool IsDelete = false,
    Guid? Batch = null);

/// <summary>送信待ちをファイルに保存し、読み込む。</summary>
public static class TransferQueueFile
{
    private const int CurrentVersion = 1;

    // 件数が多くなるので、字下げはしない
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 保存する。いったん別の名前で書いてから置き換えるので、書いている途中で落ちても前の内容は壊れない。
    /// </summary>
    public static Task SaveAsync(string path, IReadOnlyList<SavedTransferItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        return JsonFile.SaveAsync(path, new QueueDocument(CurrentVersion, items), Options, cancellationToken);
    }

    /// <summary>
    /// 読み込む。ファイルが無ければ空。壊れていれば「.broken」を付けて脇に置き、空から始める。
    /// </summary>
    public static async Task<IReadOnlyList<SavedTransferItem>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var document = await JsonFile.LoadAsync<QueueDocument>(path, Options, cancellationToken).ConfigureAwait(false);
        return document?.Items ?? [];
    }

    internal sealed record QueueDocument(int Version, IReadOnlyList<SavedTransferItem> Items);
}
