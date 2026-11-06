using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Hakobune.Core.Remote;
using Hakobune.Core.Text;

namespace Hakobune.Core.Backups;

/// <summary>控えを取ったわけ（元のファイルに何をしたか）。</summary>
public enum BackupKind
{
    /// <summary>サーバーのファイルを上書きした。</summary>
    ServerOverwritten,

    /// <summary>サーバーのファイルを消した。</summary>
    ServerDeleted,

    /// <summary>パソコンのファイルを上書きした（受け取ったとき）。</summary>
    LocalOverwritten,
}

/// <summary>控え 1 つ。</summary>
/// <param name="Id">控えの ID。</param>
/// <param name="ConnectionId">接続先の ID。</param>
/// <param name="Batch">まとめて取った操作（同期 1 回など）。</param>
/// <param name="Kind">元のファイルに何をしたか。</param>
/// <param name="OriginalPath">元のファイルの場所（サーバーのパスか、パソコンのパス）。</param>
/// <param name="StoredFile">控えのファイル（控えの置き場からの位置）。</param>
/// <param name="Size">控えの大きさ。</param>
/// <param name="OriginalModified">元のファイルの更新日時。</param>
/// <param name="TakenAt">控えを取った時刻。</param>
/// <param name="ReplacementSize">上書きしたときの、新しいファイルの大きさ（元に戻す前に、そのあと変わったかを見る）。</param>
public sealed record BackupEntry(
    Guid Id,
    Guid ConnectionId,
    Guid? Batch,
    BackupKind Kind,
    string OriginalPath,
    string StoredFile,
    long Size,
    DateTimeOffset? OriginalModified,
    DateTimeOffset TakenAt,
    long? ReplacementSize = null)
{
    /// <summary>サーバーのファイルの控えか（パソコンのファイルの控えではない）。</summary>
    [JsonIgnore]
    public bool IsOnServer => Kind != BackupKind.LocalOverwritten;
}

/// <summary>まとめて控えを取った操作（同期 1 回、削除 1 回など）。</summary>
public sealed record BackupBatch(Guid Id, Guid ConnectionId, string Label, DateTimeOffset StartedAt);

/// <summary>控えの決まり。</summary>
public sealed record BackupLimits
{
    /// <summary>上書き・削除の前に控えを取るか。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>控えを残す日数。</summary>
    public TimeSpan KeepFor { get; init; } = TimeSpan.FromDays(30);

    /// <summary>控え全体の大きさの上限。超えたら古いものから消す。</summary>
    public long MaxTotalBytes { get; init; } = 1L << 30;

    /// <summary>1 つのファイルの大きさの上限。これより大きいファイルは控えを取らない。</summary>
    public long MaxFileBytes { get; init; } = 100L << 20;
}

/// <summary>控えを取った結果。</summary>
/// <param name="Entry">取った控え（取らなかったら null）。</param>
/// <param name="Note">送信待ちの項目に出す一言（「控えあり」など。取らない設定なら null）。</param>
public readonly record struct BackupResult(BackupEntry? Entry, string? Note);

/// <summary>
/// 上書き・削除する前のファイルの控え。すべての接続先の控えを 1 か所（%LOCALAPPDATA%\Hakobune\backups）に置き、
/// 決めた日数と大きさを超えたら古いものから消す。どのスレッドから使ってもよい。
/// </summary>
/// <remarks>
/// 控えの一覧は index.jsonl に 1 行ずつ足していく（たくさん取っても、毎回ファイル全体を書き直さない）。
/// 読み込むときと古いものを消したときに、いまの中身だけで書き直す。
/// </remarks>
public sealed class BackupStore
{
    /// <summary>送信待ちの項目に出す、控えを取ったときの一言。</summary>
    public const string TakenNote = "控えあり";

    private const string IndexFileName = "index.jsonl";

    // 取ったばかりの控えは、合計の上限を超えても消さない（同じ操作の中で、取るそばから消さないように）
    private static readonly TimeSpan RecentGrace = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly Lock _gate = new();
    private readonly List<BackupEntry> _entries = [];
    private readonly Dictionary<Guid, BackupBatch> _batches = [];
    private readonly HashSet<Guid> _writtenBatches = [];
    private readonly TimeProvider _time;
    private BackupLimits _limits;

    /// <summary><paramref name="root"/> に控えを置く。</summary>
    public BackupStore(string root, BackupLimits? limits = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
        _limits = limits ?? new BackupLimits();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>控えの置き場。</summary>
    public string Root { get; }

    /// <summary>控えの決まり（設定で変わる）。</summary>
    public BackupLimits Limits
    {
        get => Volatile.Read(ref _limits);
        set => Volatile.Write(ref _limits, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>控えが増えた・減った。どのスレッドからも呼ばれる。</summary>
    public event EventHandler? Changed;

    /// <summary>控え全体の大きさ。</summary>
    public long TotalBytes
    {
        get
        {
            lock (_gate)
                return _entries.Sum(entry => entry.Size);
        }
    }

    private string IndexPath => Path.Combine(Root, IndexFileName);

    /// <summary>その接続先の控え（新しいものから）。</summary>
    public IReadOnlyList<BackupEntry> GetEntries(Guid connectionId)
    {
        lock (_gate)
            return [.. _entries.Where(entry => entry.ConnectionId == connectionId).OrderByDescending(entry => entry.TakenAt)];
    }

    /// <summary>控えを ID で探す。</summary>
    public BackupEntry? Find(Guid id)
    {
        lock (_gate)
            return _entries.Find(entry => entry.Id == id);
    }

    /// <summary>まとめて取った操作。</summary>
    public BackupBatch? GetBatch(Guid? id)
    {
        if (id is not { } value)
            return null;
        lock (_gate)
            return _batches.GetValueOrDefault(value);
    }

    /// <summary>控えのファイルの場所。</summary>
    public string GetFullPath(BackupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Path.Combine(Root, entry.StoredFile);
    }

    /// <summary>
    /// まとめて控えを取る操作を始める（同期 1 回など）。返した ID を転送の依頼に付けると、控えがまとまって並ぶ。
    /// </summary>
    public Guid BeginBatch(Guid connectionId, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        var batch = new BackupBatch(Guid.NewGuid(), connectionId, label, _time.GetUtcNow());
        lock (_gate)
            _batches[batch.Id] = batch;
        return batch.Id;
    }

    /// <summary>控えの一覧を読み込む。ファイルが無くなった控えは除き、古いものを消す。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var lines = new List<IndexLine>();
        try
        {
            if (File.Exists(IndexPath))
            {
                foreach (var text in await File.ReadAllLinesAsync(IndexPath, cancellationToken).ConfigureAwait(false))
                {
                    if (TryParse(text) is { } line)
                        lines.Add(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めなければ、控えは無いものとして続ける（控えのファイルは消さない）
        }

        lock (_gate)
        {
            _entries.Clear();
            _batches.Clear();
            _writtenBatches.Clear();
            var removed = new HashSet<Guid>();
            var seen = new HashSet<Guid>();
            foreach (var line in lines)
            {
                if (line.Batch is { } batch)
                {
                    _batches[batch.Id] = batch;
                    _writtenBatches.Add(batch.Id);
                }
                if (line.Entry is { } entry && seen.Add(entry.Id))
                    _entries.Add(entry);
                if (line.Removed is { } id)
                    removed.Add(id);
            }
            _entries.RemoveAll(entry => removed.Contains(entry.Id) || !File.Exists(GetFullPath(entry)));
        }
        Prune(rewrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 控えを取る。<paramref name="write"/> は、控えのファイルへ元の中身を書き込む処理（サーバーから受け取る、など）。
    /// 取らない設定なら何もしない。大きすぎるファイルは取らずに、そう書いた一言を返す。
    /// </summary>
    public async Task<BackupResult> SaveAsync(
        Guid connectionId,
        Guid? batch,
        BackupKind kind,
        string originalPath,
        long size,
        DateTimeOffset? originalModified,
        long? replacementSize,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalPath);
        ArgumentNullException.ThrowIfNull(write);
        var limits = Limits;
        if (!limits.IsEnabled)
            return default;
        if (size > limits.MaxFileBytes)
            return new BackupResult(null, $"大きいので控えなし（{DisplayText.Size(size)}）");

        var id = Guid.NewGuid();
        var takenAt = _time.GetUtcNow();
        var stored = StoredPathFor(connectionId, batch, id, originalPath, takenAt);
        var fullPath = Path.Combine(Root, stored);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var partial = fullPath + ".part";
        try
        {
            var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (stream.ConfigureAwait(false))
                await write(stream, cancellationToken).ConfigureAwait(false);
            File.Move(partial, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        // 控えのファイルの日時は、元のファイルの日時にする（置き場をエクスプローラーで見ても分かるように）
        if (originalModified is { } modified)
            TrySetModified(fullPath, modified);

        var entry = new BackupEntry(
            id, connectionId, batch, kind, originalPath, stored, new FileInfo(fullPath).Length, originalModified, takenAt, replacementSize);
        Add(entry);
        return new BackupResult(entry, TakenNote);
    }

    /// <summary>パソコンのファイルの控えを取る（写しを置く）。ファイルが無ければ何もしない。</summary>
    public Task<BackupResult> SaveLocalAsync(
        Guid connectionId, Guid? batch, string localPath, long? replacementSize, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        var info = new FileInfo(localPath);
        if (!info.Exists)
            return Task.FromResult(default(BackupResult));
        return SaveAsync(
            connectionId,
            batch,
            BackupKind.LocalOverwritten,
            info.FullName,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            replacementSize,
            async (destination, token) =>
            {
                var source = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                await using (source.ConfigureAwait(false))
                    await source.CopyToAsync(destination, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// サーバーのフォルダを、中身ごと控えに取る（消す前に）。<paramref name="list"/> と <paramref name="download"/> は、
    /// サーバーの一覧と受け取り。リンクはたどらない。返すのは送信待ちの項目に出す一言。
    /// </summary>
    public async Task<string?> SaveServerFolderAsync(
        Guid connectionId,
        Guid? batch,
        string folderPath,
        Func<string, CancellationToken, Task<IReadOnlyList<RemoteItem>>> list,
        Func<string, Stream, CancellationToken, Task> download,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderPath);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(download);
        if (!Limits.IsEnabled)
            return null;

        var saved = 0;
        var skipped = 0;
        var pending = new Stack<string>();
        pending.Push(folderPath);
        while (pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in await list(folder, cancellationToken).ConfigureAwait(false))
            {
                if (item.Name is "." or "..")
                    continue;
                if (item.Type == RemoteItemType.Directory)
                {
                    pending.Push(item.FullPath);
                    continue;
                }
                if (item.Type != RemoteItemType.File)
                    continue;
                var path = item.FullPath;
                var result = await SaveAsync(
                    connectionId,
                    batch,
                    BackupKind.ServerDeleted,
                    path,
                    item.Size,
                    item.Modified,
                    null,
                    (stream, token) => download(path, stream, token),
                    cancellationToken).ConfigureAwait(false);
                if (result.Entry is null)
                    skipped++;
                else
                    saved++;
            }
        }
        if (skipped > 0)
            return saved > 0 ? $"控えあり（大きい {skipped:N0} 件は控えなし）" : $"大きいので控えなし（{skipped:N0} 件）";
        return saved > 0 ? TakenNote : null;
    }

    /// <summary>控えを消す（ファイルも消す）。</summary>
    public void Delete(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var set = ids.ToHashSet();
        lock (_gate)
        {
            var removed = _entries.Where(entry => set.Contains(entry.Id)).ToList();
            if (removed.Count == 0)
                return;
            RemoveLocked(removed);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>その接続先の控えを、すべて消す。</summary>
    public void DeleteAll(Guid connectionId)
    {
        List<Guid> ids;
        lock (_gate)
            ids = [.. _entries.Where(entry => entry.ConnectionId == connectionId).Select(entry => entry.Id)];
        Delete(ids);
    }

    /// <summary>
    /// 残す日数を過ぎた控えと、合計の上限を超えたぶん（古いものから）を消す。
    /// <paramref name="rewrite"/> なら、一覧のファイルをいまの中身だけで書き直す。
    /// </summary>
    public void Prune(bool rewrite = false)
    {
        var limits = Limits;
        var now = _time.GetUtcNow();
        var changed = false;
        lock (_gate)
        {
            var removed = _entries.Where(entry => now - entry.TakenAt > limits.KeepFor).ToList();
            var total = _entries.Sum(entry => entry.Size) - removed.Sum(entry => entry.Size);
            foreach (var entry in _entries.Except(removed).OrderBy(entry => entry.TakenAt))
            {
                if (total <= limits.MaxTotalBytes || now - entry.TakenAt < RecentGrace)
                    break;
                removed.Add(entry);
                total -= entry.Size;
            }
            if (removed.Count > 0)
            {
                RemoveLocked(removed, appendToIndex: !rewrite);
                changed = true;
            }
            if (rewrite)
                RewriteLocked();
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(BackupEntry entry)
    {
        lock (_gate)
        {
            var lines = new List<IndexLine>(2);
            if (entry.Batch is { } batchId && _batches.TryGetValue(batchId, out var batch) && _writtenBatches.Add(batchId))
                lines.Add(new IndexLine { Batch = batch });
            lines.Add(new IndexLine { Entry = entry });
            AppendLocked(lines);
            _entries.Add(entry);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        Prune();
    }

    private void RemoveLocked(List<BackupEntry> removed, bool appendToIndex = true)
    {
        var ids = removed.Select(entry => entry.Id).ToHashSet();
        _entries.RemoveAll(entry => ids.Contains(entry.Id));
        foreach (var entry in removed)
        {
            var path = GetFullPath(entry);
            TryDelete(path);
            TryDeleteEmptyFolders(Path.GetDirectoryName(path));
        }
        if (appendToIndex)
            AppendLocked([.. removed.Select(entry => new IndexLine { Removed = entry.Id })]);
    }

    private void AppendLocked(IReadOnlyList<IndexLine> lines)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.AppendAllLines(IndexPath, lines.Select(line => JsonSerializer.Serialize(line, LineOptions)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一覧に書けなくても、控えのファイルは残っている。次に読み込んだとき、書けた分だけになる
        }
    }

    private void RewriteLocked()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var used = _entries.Select(entry => entry.Batch).OfType<Guid>().ToHashSet();
            var lines = new List<string>();
            foreach (var batch in _batches.Values.Where(batch => used.Contains(batch.Id)))
                lines.Add(JsonSerializer.Serialize(new IndexLine { Batch = batch }, LineOptions));
            foreach (var entry in _entries.OrderBy(entry => entry.TakenAt))
                lines.Add(JsonSerializer.Serialize(new IndexLine { Entry = entry }, LineOptions));
            var temporary = IndexPath + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, IndexPath, overwrite: true);
            _writtenBatches.Clear();
            _writtenBatches.UnionWith(used);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // 接続先\操作の時刻\短い ID_元の名前（Windows のパスの長さの上限に当たらないよう、フォルダは深くしない）
    private string StoredPathFor(Guid connectionId, Guid? batch, Guid id, string originalPath, DateTimeOffset takenAt)
    {
        var zone = _time.LocalTimeZone;
        string folder;
        lock (_gate)
        {
            folder = batch is { } batchId && _batches.TryGetValue(batchId, out var info)
                ? TimeZoneInfo.ConvertTime(info.StartedAt, zone).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + batchId.ToString("N")[..8]
                : TimeZoneInfo.ConvertTime(takenAt, zone).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }
        var name = SafeFileName(LastSegment(originalPath));
        return Path.Combine(connectionId.ToString("N"), folder, id.ToString("N")[..8] + "_" + name);
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    // Windows で使えない文字を「_」にし、長すぎる名前は拡張子を残して詰める
    private static string SafeFileName(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < ' ' || chars[i] is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                chars[i] = '_';
        }
        var result = new string(chars).TrimEnd('.', ' ');
        if (result.Length == 0)
            return "_";
        const int MaxLength = 80;
        if (result.Length <= MaxLength)
            return result;
        var extension = Path.GetExtension(result);
        if (extension.Length > 20)
            extension = "";
        return result[..(MaxLength - extension.Length)] + extension;
    }

    private static IndexLine? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonSerializer.Deserialize<IndexLine>(text, LineOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TrySetModified(string path, DateTimeOffset modified)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // 空になったフォルダを、控えの置き場の 1 つ下（接続先のフォルダ）まで消す
    private void TryDeleteEmptyFolders(string? folder)
    {
        for (var depth = 0; depth < 2 && folder is not null && !string.Equals(folder, Root, StringComparison.OrdinalIgnoreCase); depth++)
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(folder).Any())
                    return;
                Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            folder = Path.GetDirectoryName(folder);
        }
    }

    // 一覧のファイルの 1 行（どれか 1 つだけが入る）
    private sealed record IndexLine
    {
        public BackupBatch? Batch { get; init; }

        public BackupEntry? Entry { get; init; }

        public Guid? Removed { get; init; }
    }
}
