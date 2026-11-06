using Hakobune.Core.Storage;

namespace Hakobune.Core.Connections;

/// <summary>確かめた結果。</summary>
public enum TrustState
{
    /// <summary>前に信頼した鍵と同じ。</summary>
    Trusted,

    /// <summary>初めての接続先（まだ信頼していない）。</summary>
    Unknown,

    /// <summary>前に信頼した鍵と違う（なりすましのおそれがある）。</summary>
    Changed,
}

/// <summary>信頼したサーバーの鍵（SSH のホスト鍵や、FTPS の証明書）。</summary>
/// <param name="Host">ホスト名（小文字）。</param>
/// <param name="Port">ポート番号。</param>
/// <param name="Kind">鍵の種類（ssh-ed25519、x509 など）。</param>
/// <param name="Fingerprint">指紋（SSH は SHA256 の Base64、証明書は SHA256 の 16 進）。</param>
/// <param name="TrustedAt">信頼した時刻。</param>
public sealed record TrustedKey(string Host, int Port, string Kind, string Fingerprint, DateTimeOffset TrustedAt);

/// <summary>
/// 信頼したサーバーの鍵を覚えておく（SSH の known_hosts にあたる）。
/// 初めての接続先と、鍵が変わった接続先は、利用者に確かめてから信頼する。どのスレッドから使ってもよい。
/// </summary>
public sealed class TrustStore
{
    private readonly Lock _gate = new();
    private readonly List<TrustedKey> _keys;
    private readonly string? _path;
    private readonly TimeProvider _time;

    private TrustStore(string? path, IEnumerable<TrustedKey> keys, TimeProvider? timeProvider)
    {
        _path = path;
        _keys = [.. keys];
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>ファイルから読み込む。ファイルが無ければ空から始める。信頼を加えるとファイルに書く。</summary>
    public static async Task<TrustStore> LoadAsync(string path, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var document = await JsonFile.LoadAsync<Document>(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new TrustStore(path, document?.Keys ?? [], timeProvider);
    }

    /// <summary>ファイルに書かないもの（試験用）。</summary>
    public static TrustStore InMemory(TimeProvider? timeProvider = null) => new(null, [], timeProvider);

    /// <summary>信頼している鍵。</summary>
    public IReadOnlyList<TrustedKey> Keys
    {
        get
        {
            lock (_gate)
                return [.. _keys];
        }
    }

    /// <summary>この鍵を信頼してよいか確かめる。</summary>
    public (TrustState State, TrustedKey? Previous) Check(string host, int port, string kind, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        var key = Normalize(host);
        lock (_gate)
        {
            var known = _keys.FirstOrDefault(item => item.Host == key && item.Port == port && item.Kind == kind);
            if (known is null)
                return (TrustState.Unknown, null);
            return string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? (TrustState.Trusted, known)
                : (TrustState.Changed, known);
        }
    }

    /// <summary>この鍵を信頼する（同じ接続先の同じ種類の鍵は置き換える）。</summary>
    public void Trust(string host, int port, string kind, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        var key = Normalize(host);
        List<TrustedKey> snapshot;
        lock (_gate)
        {
            _keys.RemoveAll(item => item.Host == key && item.Port == port && item.Kind == kind);
            _keys.Add(new TrustedKey(key, port, kind, fingerprint, _time.GetUtcNow()));
            snapshot = [.. _keys];
        }
        if (_path is not null)
            JsonFile.Save(_path, new Document(1, snapshot));
    }

    /// <summary>この接続先について信頼した鍵を忘れる。</summary>
    public void Forget(string host, int port)
    {
        var key = Normalize(host);
        List<TrustedKey> snapshot;
        lock (_gate)
        {
            if (_keys.RemoveAll(item => item.Host == key && item.Port == port) == 0)
                return;
            snapshot = [.. _keys];
        }
        if (_path is not null)
            JsonFile.Save(_path, new Document(1, snapshot));
    }

    private static string Normalize(string host) => host.Trim().ToLowerInvariant();

    internal sealed record Document(int Version, IReadOnlyList<TrustedKey> Keys);
}
