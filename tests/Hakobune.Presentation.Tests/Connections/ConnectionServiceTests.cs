using System.Net.Security;
using Hakobune.Core.Connections;
using Hakobune.Core.Transfers;
using Hakobune.Presentation.Connections;

namespace Hakobune.Presentation.Tests.Connections;

public class ConnectionServiceTests
{
    private static readonly ConnectionProfile Profile = new() { Name = "ショップ", Host = "example.com", UserName = "web" };

    private static (ConnectionService Service, FakeLoginServer Server, ScriptedPrompts Prompts, MemorySecretStore Secrets, TrustStore Trust) Create()
    {
        var server = new FakeLoginServer();
        var prompts = new ScriptedPrompts();
        var secrets = new MemorySecretStore();
        var trust = TrustStore.InMemory();
        return (new ConnectionService(trust, secrets, prompts, server.Builder), server, prompts, secrets, trust);
    }

    [Fact]
    public async Task A_saved_password_connects_without_asking()
    {
        var (service, _, prompts, secrets, trust) = Create();
        secrets.Write(Profile.Id, "right");
        trust.Trust("example.com", 22, "ssh-ed25519", "AAAA");

        var result = await service.ConnectAsync(Profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("/home/web", result.HomeDirectory);
        Assert.Equal(0, prompts.PasswordQuestions);
        Assert.Empty(prompts.HostKeyQuestions);
        await result.DisposeQuietlyAsync();
    }

    [Fact]
    public async Task A_new_server_key_is_confirmed_once_and_remembered()
    {
        var (service, _, prompts, secrets, trust) = Create();
        secrets.Write(Profile.Id, "right");

        var first = await service.ConnectAsync(Profile, cancellationToken: TestContext.Current.CancellationToken);
        var second = await service.ConnectAsync(Profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded && second.Succeeded);
        var question = Assert.Single(prompts.HostKeyQuestions);
        Assert.False(question.IsChanged);
        Assert.Equal("AAAA", question.Fingerprint);
        Assert.Equal(TrustState.Trusted, trust.Check("example.com", 22, "ssh-ed25519", "AAAA").State);
        await first.DisposeQuietlyAsync();
        await second.DisposeQuietlyAsync();
    }

    [Fact]
    public async Task A_changed_server_key_is_reported_and_refusing_it_cancels()
    {
        var (service, server, prompts, secrets, trust) = Create();
        secrets.Write(Profile.Id, "right");
        trust.Trust("example.com", 22, "ssh-ed25519", "OLD");
        server.HostKeyFingerprint = "NEW";
        prompts.TrustHostKeys = false;

        var result = await service.ConnectAsync(Profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsCancelled);
        var question = Assert.Single(prompts.HostKeyQuestions);
        Assert.True(question.IsChanged);
        Assert.Equal("OLD", question.PreviousFingerprint);
        Assert.Equal(TrustState.Changed, trust.Check("example.com", 22, "ssh-ed25519", "NEW").State);
    }

    [Fact]
    public async Task A_wrong_password_is_asked_again_and_the_right_one_is_saved()
    {
        var (service, server, prompts, secrets, _) = Create();
        secrets.Write(Profile.Id, "old");
        prompts.Passwords.Enqueue(new PasswordAnswer("typo", Save: true));
        prompts.Passwords.Enqueue(new PasswordAnswer("right", Save: true));

        var result = await service.ConnectAsync(Profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "old", "typo", "right" }, server.PasswordsTried.ToArray());
        Assert.Equal(2, prompts.PasswordQuestions);
        Assert.Equal("right", secrets.Read(Profile.Id));
        await result.DisposeQuietlyAsync();
    }

    [Fact]
    public async Task A_password_that_should_not_be_saved_is_not_saved()
    {
        var (service, _, prompts, secrets, _) = Create();
        prompts.Passwords.Enqueue(new PasswordAnswer("right", Save: false));

        var result = await service.ConnectAsync(Profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(secrets.Read(Profile.Id));
        await result.DisposeQuietlyAsync();
    }

    [Fact]
    public async Task Not_entering_a_password_cancels()
    {
        var (service, server, _, _, _) = Create();

        var result = await service.ConnectAsync(Profile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsCancelled);
        Assert.Empty(server.PasswordsTried);
    }

    [Fact]
    public async Task An_unreachable_server_is_reported_with_the_reason()
    {
        var (service, server, _, _, _) = Create();
        server.ConnectFault = TransferFailure.Transient(FailureReason.Timeout);

        var result = await service.ConnectAsync(Profile, password: "right", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.IsCancelled);
        Assert.Equal(FailureReason.Timeout, result.Failure?.Reason);
    }

    [Fact]
    public void Certificates_trusted_by_windows_pass_without_asking()
    {
        var (service, _, prompts, _, _) = Create();
        var validate = service.CreateCertificateValidator(Profile with { Protocol = ConnectionProtocol.FtpsExplicit }, new RejectionFlag());

        Assert.True(validate(null, null, SslPolicyErrors.None));
        Assert.False(validate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.Empty(prompts.CertificateQuestions);
    }

    [Fact]
    public void Certificate_problems_are_explained_in_japanese()
    {
        var problems = ConnectionService.DescribeProblems(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, null);

        Assert.Contains("証明書の名前が、接続先の名前と一致しません", problems);
        Assert.Contains("信頼できる発行元の証明書ではありません（自己署名など）", problems);
    }
}
