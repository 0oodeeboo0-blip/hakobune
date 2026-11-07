using System.Collections.Concurrent;
using Hakobune.Core.Logging;
using Hakobune.Core.Text;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>
/// 転送の結果をログに書く（1 件ずつの完了・失敗・やり直し・取り消しと、全部終わったときのまとめ）。
/// ログが無くても、ひと区切りまでに終わった数は数える（通知に使う）。どのスレッドから呼んでもよい。
/// </summary>
internal sealed class TransferLogWriter
{
    private readonly ActivityLog? _log;
    private readonly string? _source;
    private readonly int _maxAttempts;
    private readonly ConcurrentDictionary<Guid, TransferState> _states = new();

    // まとめ（前の区切りから何件終わったか）
    private readonly Lock _gate = new();
    private TransferTotals _totals = TransferTotals.None;

    public TransferLogWriter(ActivityLog? log, string? source, int maxAttempts)
    {
        _log = log;
        _source = source;
        _maxAttempts = maxAttempts;
    }

    /// <summary>前回の残りを読み込むあいだは、状態を覚えるだけで書かない（読み込んだだけで「失敗」と書かないように）。</summary>
    public bool IsRestoring { get; set; }

    /// <summary>項目が変わった。状態が変わったときだけ書く（進み具合の変化では書かない）。</summary>
    public void OnItemChanged(TransferItemSnapshot item)
    {
        ArgumentNullException.ThrowIfNull(item);
        TransferState? previous;
        lock (_gate)
        {
            previous = _states.TryGetValue(item.Id, out var state) ? state : null;
            _states[item.Id] = item.State;
        }
        if (previous == item.State || IsRestoring)
            return;

        var upload = item.Direction == TransferDirection.Upload;
        var target = upload ? item.RemotePath : item.LocalPath;
        switch (item.State)
        {
            case TransferState.Completed:
                lock (_gate)
                {
                    var bytes = Math.Max(0, item.TotalBytes);
                    _totals = item.IsDelete ? _totals with { Deleted = _totals.Deleted + 1 }
                        : item.IsFolder ? _totals with { Folders = _totals.Folders + 1 }
                        : upload ? _totals with { Uploaded = _totals.Uploaded + 1, UploadedBytes = _totals.UploadedBytes + bytes }
                        : _totals with { Downloaded = _totals.Downloaded + 1, DownloadedBytes = _totals.DownloadedBytes + bytes };
                }
                if (_log is null)
                    break;
                // 上書き・削除の前に控えを取ったら、そう添える
                var backup = string.IsNullOrEmpty(item.Note) ? null : item.Note;
                if (item.IsDelete)
                    _log.Success($"サーバーから消しました: {target}{(backup is null ? "" : $"（{backup}）")}", _source);
                else if (item.IsFolder)
                    _log.Success($"フォルダを作りました: {target}", _source);
                else
                    _log.Success($"{(upload ? "送りました" : "受け取りました")}: {target}（{DisplayText.Size(Math.Max(0, item.TotalBytes))}{(backup is null ? "" : "・" + backup)}）", _source);
                break;
            case TransferState.Failed:
                lock (_gate)
                    _totals = _totals with { Failed = _totals.Failed + 1 };
                if (_log is null)
                    break;
                var failed = item.IsDelete ? "サーバーから消せませんでした"
                    : item.IsFolder ? "フォルダを作れませんでした"
                    : upload ? "送れませんでした" : "受け取れませんでした";
                _log.Error($"{failed}: {target}: {Reason(item)}", _source);
                break;
            case TransferState.WaitingToRetry:
                _log?.Warning($"やり直します（{item.FailedAttempts}/{_maxAttempts} 回目の失敗）: {target}: {Reason(item)}", _source);
                break;
            case TransferState.Cancelled:
                _log?.Info($"取り消しました: {target}", _source);
                break;
            case TransferState.Skipped:
                var skipped = item.IsDelete ? "消しませんでした" : upload ? "送りませんでした" : "受け取りませんでした";
                _log?.Info($"{skipped}: {target}{(item.Note is { Length: > 0 } note ? $"（{note}）" : "")}", _source);
                break;
        }
    }

    /// <summary>一覧から消えた項目は忘れる。</summary>
    public void OnItemsRemoved(IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
            _states.TryRemove(id, out _);
    }

    /// <summary>前の区切りから後に終わったものを返し、数え直す。</summary>
    public TransferTotals TakeTotals()
    {
        lock (_gate)
        {
            var totals = _totals;
            _totals = TransferTotals.None;
            return totals;
        }
    }

    /// <summary>送るものが無くなった。前の区切りから後に終わった件数をログに書いて返す（何も無ければ null）。</summary>
    public TransferTotals? OnAllDone()
    {
        var totals = TakeTotals();
        if (totals.IsEmpty)
            return null;
        var text = $"転送が終わりました: 完了 {totals.Completed} 件（{DisplayText.Size(totals.Bytes)}）";
        if (totals.Failed > 0)
            _log?.Warning($"{text}、失敗 {totals.Failed} 件", _source);
        else
            _log?.Success(text, _source);
        return totals;
    }

    private static string Reason(TransferItemSnapshot item) =>
        item.LastFailure is { } failure ? DisplayText.Describe(failure) : "理由は分かりません";
}
