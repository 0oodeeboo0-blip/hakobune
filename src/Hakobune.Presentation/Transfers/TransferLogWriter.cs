using System.Collections.Concurrent;
using Hakobune.Core.Logging;
using Hakobune.Core.Text;
using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>
/// 転送の結果をログに書く（1 件ずつの完了・失敗・やり直し・取り消しと、全部終わったときのまとめ）。どのスレッドから呼んでもよい。
/// </summary>
internal sealed class TransferLogWriter
{
    private readonly ActivityLog _log;
    private readonly string? _source;
    private readonly int _maxAttempts;
    private readonly ConcurrentDictionary<Guid, TransferState> _states = new();

    // まとめ（いつから何件終わったか）
    private readonly Lock _gate = new();
    private int _completed;
    private int _failed;
    private long _bytes;

    public TransferLogWriter(ActivityLog log, string? source, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(log);
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
                    _completed++;
                    _bytes += Math.Max(0, item.TotalBytes);
                }
                if (item.IsFolder)
                    _log.Success($"フォルダを作りました: {target}", _source);
                else
                    _log.Success($"{(upload ? "送りました" : "受け取りました")}: {target}（{DisplayText.Size(Math.Max(0, item.TotalBytes))}）", _source);
                break;
            case TransferState.Failed:
                lock (_gate)
                    _failed++;
                var failed = item.IsFolder ? "フォルダを作れませんでした" : upload ? "送れませんでした" : "受け取れませんでした";
                _log.Error($"{failed}: {target}: {Reason(item)}", _source);
                break;
            case TransferState.WaitingToRetry:
                _log.Warning($"やり直します（{item.FailedAttempts}/{_maxAttempts} 回目の失敗）: {target}: {Reason(item)}", _source);
                break;
            case TransferState.Cancelled:
                _log.Info($"取り消しました: {target}", _source);
                break;
            case TransferState.Skipped:
                _log.Info($"{(upload ? "送りませんでした" : "受け取りませんでした")}: {target}{(item.Note is { Length: > 0 } note ? $"（{note}）" : "")}", _source);
                break;
        }
    }

    /// <summary>一覧から消えた項目は忘れる。</summary>
    public void OnItemsRemoved(IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
            _states.TryRemove(id, out _);
    }

    /// <summary>送るものが無くなった。前のまとめから後に終わった件数を書く。</summary>
    public void OnAllDone()
    {
        int completed, failed;
        long bytes;
        lock (_gate)
        {
            (completed, failed, bytes) = (_completed, _failed, _bytes);
            (_completed, _failed, _bytes) = (0, 0, 0);
        }
        if (completed + failed == 0)
            return;
        var text = $"転送が終わりました: 完了 {completed} 件（{DisplayText.Size(bytes)}）";
        if (failed > 0)
            _log.Warning($"{text}、失敗 {failed} 件", _source);
        else
            _log.Success(text, _source);
    }

    private static string Reason(TransferItemSnapshot item) =>
        item.LastFailure is { } failure ? DisplayText.Describe(failure) : "理由は分かりません";
}
