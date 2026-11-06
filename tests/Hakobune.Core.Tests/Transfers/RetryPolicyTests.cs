using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Transfers;

public class RetryPolicyTests
{
    private static readonly TransferFailure Transient = new(FailureReason.ConnectionLost, FailureKind.Transient);
    private static readonly TransferFailure Permanent = new(FailureReason.PermissionDenied, FailureKind.Permanent);

    [Fact]
    public void Transient_failures_are_retried_until_the_limit()
    {
        var policy = new RetryPolicy(maxAttempts: 3, delays: [TimeSpan.FromSeconds(1)]);

        Assert.True(policy.ShouldRetry(Transient, failedAttempts: 1));
        Assert.True(policy.ShouldRetry(Transient, failedAttempts: 2));
        Assert.False(policy.ShouldRetry(Transient, failedAttempts: 3));
    }

    [Fact]
    public void Permanent_failures_are_never_retried()
    {
        Assert.False(RetryPolicy.Default.ShouldRetry(Permanent, failedAttempts: 1));
    }

    [Fact]
    public void Default_policy_tries_five_times()
    {
        Assert.Equal(5, RetryPolicy.Default.MaxAttempts);
        Assert.True(RetryPolicy.Default.ShouldRetry(Transient, failedAttempts: 4));
        Assert.False(RetryPolicy.Default.ShouldRetry(Transient, failedAttempts: 5));
    }

    [Fact]
    public void Delays_follow_the_schedule_and_repeat_the_last_value()
    {
        var policy = new RetryPolicy(
            maxAttempts: 10,
            delays: [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)],
            jitterRatio: 0);

        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(7));
    }

    [Fact]
    public void Jitter_stays_within_the_ratio()
    {
        var policy = new RetryPolicy(maxAttempts: 5, delays: [TimeSpan.FromSeconds(10)], jitterRatio: 0.2);
        var random = new Random(12345);

        for (var i = 0; i < 1000; i++)
        {
            var delay = policy.GetDelay(1, random);
            Assert.InRange(delay, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Failed_attempt_counts_start_at_one(int failedAttempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Default.GetDelay(failedAttempts));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Default.ShouldRetry(Transient, failedAttempts));
    }

    [Fact]
    public void Constructor_validates_its_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0, [TimeSpan.FromSeconds(1)]));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(1, [TimeSpan.FromSeconds(-1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(1, [TimeSpan.FromSeconds(1)], jitterRatio: 1.5));
    }
}
