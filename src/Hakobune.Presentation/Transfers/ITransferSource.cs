using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>転送の一覧に出す送信待ち。ふだんは <see cref="TransferController"/>。</summary>
public interface ITransferSource
{
    /// <summary>項目が加わった、または状態や進み具合が変わった（どのスレッドからも呼ばれうる）。</summary>
    event EventHandler<TransferItemSnapshot>? ItemChanged;

    /// <summary>項目が一覧から消えた（どのスレッドからも呼ばれうる）。</summary>
    event EventHandler<IReadOnlyList<Guid>>? ItemsRemoved;

    /// <summary>一時停止した、再開した、など（どのスレッドからも呼ばれうる）。</summary>
    event EventHandler? StateChanged;

    /// <summary>いまのすべての項目。</summary>
    IReadOnlyList<TransferItemSnapshot> GetItems();

    /// <summary>1 件を試す回数の上限（「2/5回目」の 5）。</summary>
    int MaxAttempts { get; }

    /// <summary>同時に転送する数。</summary>
    int MaxConcurrency { get; }

    /// <summary>一時停止しているか（利用者が止めた、または接続できなくて止まった）。</summary>
    bool IsPaused { get; }

    /// <summary>接続できなくて止まったときの理由。利用者が止めたときは null。</summary>
    TransferFailure? StopReason { get; }

    /// <summary>前回の残りを読み込んで止めてある件数（再開するまで。なければ 0）。</summary>
    int RestoredPendingCount { get; }

    /// <summary>すべての転送を一時停止する（転送中のものは待機中に戻り、再開すると続きから送る）。</summary>
    void Pause();

    /// <summary>再開する。</summary>
    void Resume();

    /// <summary>失敗した項目を待機中に戻す。戻した件数を返す。</summary>
    int RetryFailed();

    /// <summary>失敗・取り消しの項目を待機中に戻す。</summary>
    int Retry(IEnumerable<Guid> ids);

    /// <summary>やり直しを待っている項目を、すぐやり直す。</summary>
    int RetryNow(IEnumerable<Guid> ids);

    /// <summary>項目を取り消す。</summary>
    int Cancel(IEnumerable<Guid> ids);

    /// <summary>終わった項目（完了・スキップ・取り消し）を一覧から消す。</summary>
    int RemoveFinished();
}

/// <summary>項目のない送信待ち（まだどこにも接続していないとき）。</summary>
public sealed class EmptyTransferSource : ITransferSource
{
    /// <summary>共有のもの。</summary>
    public static EmptyTransferSource Instance { get; } = new();

    /// <inheritdoc />
    public event EventHandler<TransferItemSnapshot>? ItemChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<Guid>>? ItemsRemoved
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public int MaxAttempts => RetryPolicy.Default.MaxAttempts;

    /// <inheritdoc />
    public int MaxConcurrency => new TransferOptions().MaxConcurrency;

    /// <inheritdoc />
    public bool IsPaused => false;

    /// <inheritdoc />
    public TransferFailure? StopReason => null;

    /// <inheritdoc />
    public int RestoredPendingCount => 0;

    /// <inheritdoc />
    public IReadOnlyList<TransferItemSnapshot> GetItems() => [];

    /// <inheritdoc />
    public void Pause()
    {
    }

    /// <inheritdoc />
    public void Resume()
    {
    }

    /// <inheritdoc />
    public int RetryFailed() => 0;

    /// <inheritdoc />
    public int Retry(IEnumerable<Guid> ids) => 0;

    /// <inheritdoc />
    public int RetryNow(IEnumerable<Guid> ids) => 0;

    /// <inheritdoc />
    public int Cancel(IEnumerable<Guid> ids) => 0;

    /// <inheritdoc />
    public int RemoveFinished() => 0;
}
