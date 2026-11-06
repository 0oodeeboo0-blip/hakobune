using System.Text;

namespace Hakobune.Core.Remote.Sftp;

/// <summary>
/// サーバーの鍵（ホスト鍵）を確かめる。受け入れるなら true を返す。
/// </summary>
/// <param name="algorithm">鍵の種類（ssh-ed25519 など）。</param>
/// <param name="sha256Fingerprint">SHA256 の指紋（ssh コマンドと同じ表記。「SHA256:」は付かない）。</param>
/// <param name="keyLength">鍵の長さ（ビット）。</param>
public delegate bool HostKeyValidator(string algorithm, string sha256Fingerprint, int keyLength);

/// <summary>SFTP の接続先の設定。</summary>
public sealed record SftpConnectionSettings
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string UserName { get; init; }

    /// <summary>パスワード。秘密鍵だけで入るときは null。</summary>
    public string? Password { get; init; }

    /// <summary>秘密鍵ファイル（OpenSSH・PuTTY 形式など）のパス。</summary>
    public string? PrivateKeyPath { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>
    /// サーバーの鍵の確かめ方（必須）。初めての接続先や、鍵が変わった接続先は、利用者に確認してから受け入れる。
    /// </summary>
    public required HostKeyValidator ValidateHostKey { get; init; }

    /// <summary>ファイル名の文字コード。null なら UTF-8。</summary>
    public Encoding? FileNameEncoding { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    // パスワードなどがログに出ないようにする。
    public override string ToString() =>
        $"{nameof(SftpConnectionSettings)} {{ Host = {Host}, Port = {Port}, UserName = {UserName} }}";
}
