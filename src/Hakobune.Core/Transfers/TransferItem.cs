namespace Hakobune.Core.Transfers;

/// <summary>
/// 送信待ちの 1 件。中身の書き換えは <see cref="TransferScheduler"/> がロックの中で行う。
/// </summary>
internal sealed class TransferItem(long sequence, TransferRequest request)
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public long Sequence { get; } = sequence;

    public TransferRequest Request { get; } = request;

    public TransferState State { get; set; } = TransferState.Queued;

    public int FailedAttempts { get; set; }

    public TransferFailure? LastFailure { get; set; }

    public DateTimeOffset? RetryAt { get; set; }

    public long TotalBytes { get; set; }

    public long TransferredBytes { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// この項目が転送先へ書き込みを始めたか。やり直しのとき、自分の書きかけを
    /// 「同じ名前のファイル」と取り違えないために使う。
    /// </summary>
    public bool HasWrittenTarget { get; set; }

    /// <summary>最後に進み具合を知らせた時刻（<see cref="TimeProvider.GetTimestamp"/> の値）。</summary>
    public long LastProgressTimestamp { get; set; }

    public TransferItemSnapshot ToSnapshot() => new(
        Id,
        Sequence,
        Request.Direction,
        Request.LocalPath,
        Request.RemotePath,
        State,
        FailedAttempts,
        LastFailure,
        RetryAt,
        TotalBytes,
        TransferredBytes,
        Note);
}

/// <summary>IProgress を、呼ばれたその場で処理する。</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
