namespace Hakobune.Core.Transfers;

/// <summary>
/// 失敗したファイルを、いつ・何回までやり直すかの決まり。
/// </summary>
/// <remarks>
/// 一時的な失敗だけを、そのファイルのまま間隔をあけてやり直す。
/// FFFTP のように待ち行列の最後へ回したり、上限なしに繰り返したりはしない。
/// </remarks>
public sealed class RetryPolicy
{
    private readonly TimeSpan[] _delays;

    /// <summary>
    /// 既定の決まり。最初の 1 回を含めて最大 5 回試し、失敗するたびに 2 秒・5 秒・15 秒・30 秒待つ。
    /// </summary>
    public static RetryPolicy Default { get; } = new(
        maxAttempts: 5,
        delays: [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)]);

    /// <param name="maxAttempts">最初の 1 回を含めた、試す回数の上限。</param>
    /// <param name="delays">n 回目の失敗のあとに待つ時間。失敗の回数が要素数より多いときは最後の値を使う。</param>
    /// <param name="jitterRatio">
    /// 待ち時間をばらつかせる割合（0〜1）。いくつもの転送が同時にやり直してサーバーに負担をかけないようにする。
    /// </param>
    public RetryPolicy(int maxAttempts, IReadOnlyList<TimeSpan> delays, double jitterRatio = 0.2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentNullException.ThrowIfNull(delays);
        if (delays.Count == 0)
            throw new ArgumentException("待ち時間を 1 つ以上指定してください。", nameof(delays));
        foreach (var delay in delays)
        {
            if (delay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delays), "待ち時間に負の値は指定できません。");
        }
        if (double.IsNaN(jitterRatio) || jitterRatio < 0 || jitterRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(jitterRatio), "0 から 1 の値を指定してください。");

        MaxAttempts = maxAttempts;
        JitterRatio = jitterRatio;
        _delays = [.. delays];
    }

    /// <summary>最初の 1 回を含めた、試す回数の上限。</summary>
    public int MaxAttempts { get; }

    /// <summary>待ち時間をばらつかせる割合。</summary>
    public double JitterRatio { get; }

    /// <summary>
    /// <paramref name="failedAttempts"/> 回失敗したあと、もう一度試すべきか。
    /// </summary>
    public bool ShouldRetry(TransferFailure failure, int failedAttempts)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        return failure.IsTransient && failedAttempts < MaxAttempts;
    }

    /// <summary>
    /// <paramref name="failedAttempts"/> 回失敗したあと、次に試すまでの待ち時間。
    /// </summary>
    /// <param name="failedAttempts">これまでに失敗した回数（1 以上）。</param>
    /// <param name="random">ばらつきに使う乱数。省略すると共有の乱数を使う。</param>
    public TimeSpan GetDelay(int failedAttempts, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);

        var baseDelay = _delays[Math.Min(failedAttempts, _delays.Length) - 1];
        if (JitterRatio == 0 || baseDelay == TimeSpan.Zero)
            return baseDelay;

        var rnd = random ?? Random.Shared;
        var factor = 1 + ((rnd.NextDouble() * 2) - 1) * JitterRatio;
        return TimeSpan.FromTicks((long)(baseDelay.Ticks * factor));
    }
}
