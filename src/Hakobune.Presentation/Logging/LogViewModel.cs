using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakobune.Core.Logging;

namespace Hakobune.Presentation.Logging;

/// <summary>ログの 1 行（画面用）。</summary>
public sealed class LogRowViewModel
{
    public LogRowViewModel(LogEntry entry, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(timeZone);
        Entry = entry;
        LocalTime = TimeZoneInfo.ConvertTime(entry.Time, timeZone);
    }

    public LogEntry Entry { get; }

    /// <summary>書いた時刻（パソコンの時刻）。</summary>
    public DateTimeOffset LocalTime { get; }

    /// <summary>時刻（例: 08:16:03）。</summary>
    public string TimeText => LocalTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string Message => Entry.Message;

    public LogKind Kind => Entry.Kind;

    public bool IsDetail => Entry.Kind == LogKind.Detail;

    public bool IsInfo => Entry.Kind == LogKind.Info;

    public bool IsSuccess => Entry.Kind == LogKind.Success;

    public bool IsWarning => Entry.Kind == LogKind.Warning;

    public bool IsError => Entry.Kind == LogKind.Error;

    /// <summary>読み上げ用（例: 失敗、08:16:03、送れませんでした…）。</summary>
    public string AccessibleText => $"{KindName(Entry.Kind)}、{TimeText}、{Message}";

    /// <summary>コピー・保存するときの 1 行（例: 2026/11/06 08:16:03  失敗  [ショップ]  送れませんでした…）。</summary>
    public string ToText()
    {
        var source = Entry.Source is { Length: > 0 } name ? $"[{name}]  " : "";
        return $"{LocalTime.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)}  {KindName(Entry.Kind),-4}  {source}{Message}";
    }

    internal static string KindName(LogKind kind) => kind switch
    {
        LogKind.Detail => "詳細",
        LogKind.Success => "成功",
        LogKind.Warning => "注意",
        LogKind.Error => "失敗",
        _ => "情報",
    };
}

/// <summary>
/// 下の「ログ」。接続・転送・ファイルの操作を新しいものが下に来るように並べる。通信の詳細（コマンドとサーバーの返事）は、選んだときだけ出す。
/// </summary>
public sealed class LogViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);

    private readonly ActivityLog _log;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly Lock _flushLock = new();
    private readonly ITimer? _flushTimer;
    private long _shownThrough;
    private int _flushScheduled;
    private bool _disposed;

    /// <summary>
    /// <paramref name="context"/> は画面のスレッド（null なら書かれたスレッドでそのまま反映する。試験用）。
    /// </summary>
    public LogViewModel(ActivityLog log, TimeProvider? timeProvider = null, SynchronizationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _context = context;
        if (context is not null)
            _flushTimer = _time.CreateTimer(_ => Post(Flush), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ClearCommand = new RelayCommand(Clear, () => Rows.Count > 0);
        _log.Added += OnAdded;
        _log.Cleared += OnCleared;
        Rebuild();
    }

    /// <summary>出している行（古い順）。</summary>
    public ObservableList<LogRowViewModel> Rows { get; } = [];

    /// <summary>通信の詳細（FTP のコマンドとサーバーの返事など）も出すか。</summary>
    public bool ShowsDetails
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                Rebuild();
        }
    }

    /// <summary>出すものが無い。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>何も無いときの説明。</summary>
    public string EmptyText => "まだ記録はありません。接続や転送をすると、ここに出ます";

    /// <summary>ログを全部消す。</summary>
    public IRelayCommand ClearCommand { get; }

    /// <summary>行が下に増えた（画面は、いちばん下を見ていればついていく）。</summary>
    public event EventHandler? RowsAppended;

    /// <summary>出している行を、コピー・保存する形の文字にする。</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        foreach (var row in Rows)
            text.AppendLine(row.ToText());
        return text.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _log.Added -= OnAdded;
        _log.Cleared -= OnCleared;
        _flushTimer?.Dispose();
    }

    /// <summary>ためてある行を反映する。</summary>
    internal void Flush()
    {
        lock (_flushLock)
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            if (_disposed)
                return;
            var batch = new List<LogEntry>();
            while (_pending.TryDequeue(out var entry))
            {
                // 作り直したときに、もう一覧に入れたもの
                if (entry.Sequence > _shownThrough)
                    batch.Add(entry);
            }
            if (batch.Count == 0)
                return;

            // 同時に書かれたものは、書いた順に並べ直す
            batch.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            var wasEmpty = Rows.Count == 0;
            var added = false;
            foreach (var entry in batch)
            {
                if (!IsShown(entry))
                    continue;
                Rows.Add(new LogRowViewModel(entry, _time.LocalTimeZone));
                added = true;
            }

            // ログが忘れた分は、画面からも消す
            var overflow = Rows.Count - _log.Capacity;
            for (var i = 0; i < overflow; i++)
                Rows.RemoveAt(0);

            if (!added)
                return;
            if (wasEmpty)
            {
                OnPropertyChanged(nameof(IsEmpty));
                ClearCommand.NotifyCanExecuteChanged();
            }
            RowsAppended?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsShown(LogEntry entry) => ShowsDetails || entry.Kind != LogKind.Detail;

    private void Clear()
    {
        _log.Clear();
        Rebuild();
    }

    // ログ全体から作り直す（通信の詳細を出す・出さないを変えたときと、消したとき）
    private void Rebuild()
    {
        lock (_flushLock)
        {
            if (_disposed)
                return;
            var snapshot = _log.GetSnapshot();
            _shownThrough = snapshot.LastSequence;
            Rows.ReplaceAll(snapshot.Entries.Where(IsShown).Select(entry => new LogRowViewModel(entry, _time.LocalTimeZone)));
            OnPropertyChanged(nameof(IsEmpty));
            ClearCommand.NotifyCanExecuteChanged();
        }
        RowsAppended?.Invoke(this, EventArgs.Empty);
    }

    private void OnAdded(object? sender, LogEntry entry)
    {
        _pending.Enqueue(entry);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1)
            return;
        if (_flushTimer is null)
            Flush();
        else
            _flushTimer.Change(FlushInterval, Timeout.InfiniteTimeSpan); // 細かく書かれても、まとめて反映する
    }

    private void OnCleared(object? sender, EventArgs e) => Post(Rebuild);

    private void Post(Action action)
    {
        if (_context is null)
            action();
        else
            _context.Post(_ => action(), null);
    }
}
