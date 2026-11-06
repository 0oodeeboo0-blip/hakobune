using Hakobune.Core.Text;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Text;

public class DisplayTextTests
{
    [Fact]
    public void Every_failure_reason_has_its_own_description()
    {
        var descriptions = Enum.GetValues<FailureReason>()
            .Where(reason => reason != FailureReason.Unknown)
            .Select(reason => DisplayText.Describe(TransferFailure.Permanent(reason)))
            .ToList();

        Assert.All(descriptions, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(descriptions.Count, descriptions.Distinct().Count());
        Assert.DoesNotContain(DisplayText.Describe(TransferFailure.Permanent(FailureReason.Unknown)), descriptions);
    }

    [Theory]
    [InlineData(550, "Permission denied.", "権限がありません（550）")]
    [InlineData(552, "Disk full.", "サーバーの容量が足りません（552）")]
    [InlineData(550, "No such file or directory", "見つかりません（550）")]
    public void Ftp_reply_codes_are_shown_with_the_reason(int code, string message, string expected)
    {
        Assert.Equal(expected, DisplayText.Describe(FtpReplyClassifier.Classify(code, message)));
    }

    [Fact]
    public void Retry_reasons_read_as_a_cause()
    {
        Assert.Equal("接続が切れたため", DisplayText.RetryReason(TransferFailure.Transient(FailureReason.ConnectionLost)));
        Assert.Equal("失敗したため", DisplayText.RetryReason(null));
    }

    [Theory]
    [InlineData(0L, "0 バイト")]
    [InlineData(512L, "512 バイト")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(9_728L, "9.5 KB")]
    [InlineData(47_104L, "46 KB")]
    [InlineData(1_023_000L, "999 KB")]
    [InlineData(1_024_000L, "1.0 MB")]
    [InlineData(6_606_029L, "6.3 MB")]
    [InlineData(134_637_158L, "128.4 MB")]
    [InlineData(5_368_709_120L, "5.0 GB")]
    [InlineData(-1L, "")]
    public void Sizes_are_short_and_readable(long bytes, string expected)
    {
        Assert.Equal(expected, DisplayText.Size(bytes));
    }

    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1L, "1 KB")]
    [InlineData(1_024L, "1 KB")]
    [InlineData(1_025L, "2 KB")]
    [InlineData(18_432L, "18 KB")]
    [InlineData(1_000_000L, "977 KB")]
    [InlineData(134_637_158L, "128.4 MB")]
    public void File_list_sizes_round_up_to_kilobytes_like_explorer(long bytes, string expected)
    {
        Assert.Equal(expected, DisplayText.FileSize(bytes));
    }

    [Theory]
    [InlineData(0d, "")]
    [InlineData(double.NaN, "")]
    [InlineData(500d, "500 B/s")]
    [InlineData(3_565_158d, "3.4 MB/s")]
    [InlineData(6_606_029d, "6.3 MB/s")]
    public void Speeds_show_bytes_per_second(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, DisplayText.Speed(bytesPerSecond));
    }

    [Theory]
    [InlineData(0d, "")]
    [InlineData(1.9d, "残り 2秒")]
    [InlineData(10d, "残り 10秒")]
    [InlineData(41d, "残り 約45秒")]
    [InlineData(58d, "残り 約1分")]
    [InlineData(210d, "残り 約4分")]
    [InlineData(3600d, "残り 約1時間")]
    [InlineData(4801d, "残り 約1時間21分")]
    public void Remaining_time_is_rounded_up(double seconds, string expected)
    {
        Assert.Equal(expected, DisplayText.Remaining(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Every_state_has_a_label()
    {
        Assert.All(Enum.GetValues<TransferState>(), state => Assert.NotEqual("", DisplayText.StateLabel(state)));
        Assert.Equal("再試行待ち", DisplayText.StateLabel(TransferState.WaitingToRetry));
    }
}
