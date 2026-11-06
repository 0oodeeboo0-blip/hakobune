using System.Text;
using Hakobune.Core.Remote;
using Hakobune.Core.Remote.Sftp;
using Hakobune.Core.Tests.Fakes;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Tests.Remote;

/// <summary>秘密鍵のパスフレーズとファイルの問題が、それとわかる失敗になるか（サーバーには接続しない）。</summary>
public sealed class SftpPrivateKeyTests : IDisposable
{
    // パスフレーズ「hakobune-key-pass」で暗号化した、テストだけで使う ed25519 の鍵
    private const string LockedKey = """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAACmFlczI1Ni1jdHIAAAAGYmNyeXB0AAAAGAAAABB0
        0aYnddrWaRMkvEt7L+XoAAAAEAAAAAEAAAAzAAAAC3NzaC1lZDI1NTE5AAAAIGso
        xxnchNsEGhTLadX73XVg7p0aIgYuTzlS0prdRHv5AAAAkDyy+omQAOHZ6YlwPqCe
        54AoMpKz5tI4hpZDCiMyYS0RF6Z8OChkvzMhz1u+GRFhTpEyNJPCLcMSD6f1z9mR
        LVa0RlP63i1Siefw3AUvSY0BGbp05ORBdvcfs5hlK7W9d+BoqVtbVfY+k/cePviV
        ggo7iS8XpHQOp3eJSolgbx8u1+jUpzTb6bw17vQndflzWA==
        -----END OPENSSH PRIVATE KEY-----

        """;

    private const string Passphrase = "hakobune-key-pass";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ポート 9 には何もいないので、鍵を読めたあとは接続の失敗になる
    private static IRemoteSession CreateSession(string keyPath, string? passphrase) =>
        new SftpSessionFactory(new SftpConnectionSettings
        {
            Host = "127.0.0.1",
            Port = 9,
            UserName = "test",
            PrivateKeyPath = keyPath,
            PrivateKeyPassphrase = passphrase,
            ValidateHostKey = (_, _, _) => true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }).Create();

    [Theory]
    [InlineData(null)]
    [InlineData("wrong passphrase")]
    public async Task A_locked_key_without_the_right_passphrase_asks_for_it(string? passphrase)
    {
        var path = _temp.CreateFile("id_ed25519", Encoding.ASCII.GetBytes(LockedKey));
        await using var session = CreateSession(path, passphrase);

        var error = await Assert.ThrowsAsync<RemoteOperationException>(() => session.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(FailureReason.KeyPassphraseRequired, error.Failure.Reason);
    }

    [Fact]
    public async Task The_right_passphrase_opens_the_key()
    {
        var path = _temp.CreateFile("id_ed25519", Encoding.ASCII.GetBytes(LockedKey));
        await using var session = CreateSession(path, Passphrase);

        var error = await Assert.ThrowsAsync<RemoteOperationException>(() => session.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.NotEqual(FailureReason.KeyPassphraseRequired, error.Failure.Reason);
        Assert.NotEqual(FailureReason.KeyFileUnreadable, error.Failure.Reason);
    }

    [Fact]
    public async Task A_missing_key_file_is_explained()
    {
        await using var session = CreateSession(Path.Combine(_temp.Path, "id_missing"), null);

        var error = await Assert.ThrowsAsync<RemoteOperationException>(() => session.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(FailureReason.LocalFileNotFound, error.Failure.Reason);
    }

    [Fact]
    public async Task A_file_that_is_not_a_key_is_explained()
    {
        var path = _temp.CreateFile("id_broken", Encoding.ASCII.GetBytes("this is not a key\n"));
        await using var session = CreateSession(path, null);

        var error = await Assert.ThrowsAsync<RemoteOperationException>(() => session.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(FailureReason.KeyFileUnreadable, error.Failure.Reason);
    }
}
