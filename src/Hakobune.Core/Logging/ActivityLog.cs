namespace Hakobune.Core.Logging;

/// <summary>ログの種類。</summary>
public enum LogKind
{
    /// <summary>通信の詳細（FTP のコマンドとサーバーの返事など）。ふだんは画面に出さない。</summary>
    Detail,

    /// <summary>ふつうの出来事。</summary>
    Info,

    /// <summary>うまくいった（接続した、送り終わった）。</summary>
    Success,

    /// <summary>気をつけること（やり直す、など）。</summary>
    Warning,

    /// <summary>失敗。</summary>
    Error,
}

/// <summary>ある時点のログ全体。</summary>
/// <param name="Entries">覚えているもの（古い順）。</param>
/// <param name="LastSequence">それまでに書いた最後の番号（消したあとでも続きの番号）。これより大きい番号は、あとから書かれたもの。</param>
public sealed record LogSnapshot(IReadOnlyList<LogEntry> Entries, long LastSequence);

/// <summary>ログの 1 行。</summary>
/// <param name="Sequence">書いた順の番号（1 から）。</param>
/// <param name="Time">書いた時刻（UTC）。</param>
/// <param name="Kind">種類。</param>
/// <param name="Message">内容（利用者向けの日本語。通信の詳細はサーバーとのやり取りそのまま）。</param>
/// <param name="Source">どの接続先のことか（接続先の名前）。分からなければ null。</param>
public sealed record LogEntry(long Sequence, DateTimeOffset Time, LogKind Kind, string Message, string? Source = null);

/// <summary>
/// アプリのログ（接続、転送、ファイルの操作、通信の詳細）。新しいものから決まった数だけ覚えておく。どのスレッドから書いてもよい。
/// </summary>
public sealed class ActivityLog
{
    /// <summary>覚えておく行の数の既定（数百ファイルを送っても、通信の詳細まで残る数）。</summary>
    public const int DefaultCapacity = 20_000;

    private readonly Lock _lock = new();
    private readonly LogEntry[] _buffer;
    private readonly TimeProvider _time;
    private int _start;
    private int _count;
    private long _sequence;

    /// <summary>ログを作る。<paramref name="capacity"/> を超えたら古いものから忘れる。</summary>
    public ActivityLog(int capacity = DefaultCapacity, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new LogEntry[capacity];
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>1 行書かれた（書いたスレッドで呼ばれる）。</summary>
    public event EventHandler<LogEntry>? Added;

    /// <summary>消された（消したスレッドで呼ばれる）。</summary>
    public event EventHandler? Cleared;

    /// <summary>覚えておける行の数。</summary>
    public int Capacity => _buffer.Length;

    /// <summary>1 行書く。改行は空白にして 1 行にする。</summary>
    public LogEntry Add(LogKind kind, string message, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogEntry entry;
        lock (_lock)
        {
            entry = new LogEntry(++_sequence, _time.GetUtcNow(), kind, OneLine(message), source);
            var index = (_start + _count) % _buffer.Length;
            _buffer[index] = entry;
            if (_count < _buffer.Length)
                _count++;
            else
                _start = (_start + 1) % _buffer.Length;
        }
        Added?.Invoke(this, entry);
        return entry;
    }

    /// <summary>ふつうの出来事を書く。</summary>
    public LogEntry Info(string message, string? source = null) => Add(LogKind.Info, message, source);

    /// <summary>うまくいったことを書く。</summary>
    public LogEntry Success(string message, string? source = null) => Add(LogKind.Success, message, source);

    /// <summary>気をつけることを書く。</summary>
    public LogEntry Warning(string message, string? source = null) => Add(LogKind.Warning, message, source);

    /// <summary>失敗を書く。</summary>
    public LogEntry Error(string message, string? source = null) => Add(LogKind.Error, message, source);

    /// <summary>通信の詳細を書く。</summary>
    public LogEntry Detail(string message, string? source = null) => Add(LogKind.Detail, message, source);

    /// <summary>いま覚えているものを、古い順に返す。</summary>
    public LogSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var entries = new LogEntry[_count];
            for (var i = 0; i < _count; i++)
                entries[i] = _buffer[(_start + i) % _buffer.Length];
            return new LogSnapshot(entries, _sequence);
        }
    }

    /// <summary>全部消す（番号は続きから振る）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_buffer);
            _start = 0;
            _count = 0;
        }
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private static string OneLine(string message)
    {
        var trimmed = message.Trim();
        if (trimmed.IndexOfAny(['\r', '\n']) < 0)
            return trimmed;
        return string.Join(" ", trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
