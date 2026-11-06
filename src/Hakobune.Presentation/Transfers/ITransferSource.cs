using Hakobune.Core.Transfers;

namespace Hakobune.Presentation.Transfers;

/// <summary>転送の一覧に出す送信待ち。ふだんは <see cref="TransferScheduler"/>。</summary>
public interface ITransferSource
{
    /// <summary>項目が加わった、または状態や進み具合が変わった（どのスレッドからも呼ばれうる）。</summary>
    event EventHandler<TransferItemSnapshot>? ItemChanged;

    /// <summary>いまのすべての項目。</summary>
    IReadOnlyList<TransferItemSnapshot> GetItems();

    /// <summary>1 件を試す回数の上限（「2/5回目」の 5）。</summary>
    int MaxAttempts { get; }

    /// <summary>同時に転送する数。</summary>
    int MaxConcurrency { get; }

    /// <summary>失敗した項目を待機中に戻す。戻した件数を返す。</summary>
    int RetryFailed();
}

/// <summary><see cref="TransferScheduler"/> をそのまま一覧に出す。</summary>
public sealed class SchedulerTransferSource(TransferScheduler scheduler) : ITransferSource
{
    private readonly TransferScheduler _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    /// <inheritdoc />
    public event EventHandler<TransferItemSnapshot>? ItemChanged
    {
        add => _scheduler.ItemChanged += value;
        remove => _scheduler.ItemChanged -= value;
    }

    /// <inheritdoc />
    public int MaxAttempts => _scheduler.Options.RetryPolicy.MaxAttempts;

    /// <inheritdoc />
    public int MaxConcurrency => _scheduler.Options.MaxConcurrency;

    /// <inheritdoc />
    public IReadOnlyList<TransferItemSnapshot> GetItems() => _scheduler.GetItems();

    /// <inheritdoc />
    public int RetryFailed() => _scheduler.RetryFailed();
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
    public int MaxAttempts => RetryPolicy.Default.MaxAttempts;

    /// <inheritdoc />
    public int MaxConcurrency => new TransferOptions().MaxConcurrency;

    /// <inheritdoc />
    public IReadOnlyList<TransferItemSnapshot> GetItems() => [];

    /// <inheritdoc />
    public int RetryFailed() => 0;
}
