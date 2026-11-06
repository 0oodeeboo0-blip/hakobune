using Hakobune.Core.Remote;
using Hakobune.Core.Transfers;

namespace Hakobune.Core.Browsing;

/// <summary>
/// 一覧を取った結果。失敗しても、前回の一覧があればそれを <see cref="IsStale"/> つきで返す。
/// </summary>
/// <param name="Path">フォルダのパス。</param>
/// <param name="Items">中身。</param>
/// <param name="IsStale">前回の一覧を代わりに返しているか（最新ではない）。</param>
/// <param name="Failure">取得に失敗したときの理由。成功していれば null。</param>
/// <param name="RetrievedAt">この一覧を実際に取った時刻。</param>
public sealed record BrowseResult(
    string Path,
    IReadOnlyList<RemoteItem> Items,
    bool IsStale,
    TransferFailure? Failure,
    DateTimeOffset RetrievedAt)
{
    /// <summary>最新の一覧を取れたか。</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// 画面でサーバーの中を見るための接続。転送用の接続とは別に、1 本だけ持つ。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>操作は 1 つずつ順番に行い、同じ接続を同時に使わない（FFFTP の「接続の取り合い」を防ぐ）。</item>
/// <item>一覧の取得に失敗しても、前回の一覧を消さずに返す（FFFTP の「一覧が空白になる」を防ぐ）。</item>
/// <item>切れた接続は捨てて、次の操作のときに自動でつなぎ直す。</item>
/// </list>
/// </remarks>
public sealed class RemoteBrowser : IAsyncDisposable
{
    private readonly IRemoteSessionFactory _sessionFactory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, BrowseResult> _lastGood = new(StringComparer.Ordinal);
    private IRemoteSession? _session;

    public RemoteBrowser(IRemoteSessionFactory sessionFactory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        _sessionFactory = sessionFactory;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// フォルダの一覧を取る。接続が切れていたらつなぎ直して 1 回だけやり直す。
    /// それでも失敗したら、前回の一覧（あれば）を返す。例外は中止のときだけ投げる。
    /// </summary>
    public async Task<BrowseResult> ListAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TransferFailure? failure = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
                    var items = await session.ListAsync(path, cancellationToken).ConfigureAwait(false);
                    var result = new BrowseResult(path, items, IsStale: false, Failure: null, _time.GetUtcNow());
                    _lastGood[path] = result;
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = FailureClassifier.FromException(ex);
                    await DropBrokenSessionAsync(failure).ConfigureAwait(false);
                    if (!failure.IsTransient)
                        break;
                }
            }

            return _lastGood.TryGetValue(path, out var previous)
                ? previous with { IsStale = true, Failure = failure }
                : new BrowseResult(path, [], IsStale: false, failure, _time.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>ログイン直後のフォルダ（ホーム）。</summary>
    public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken) =>
        RunAsync(session => session.GetHomeDirectoryAsync(cancellationToken), cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(async session =>
        {
            await session.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(async session =>
        {
            await session.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task RenameAsync(string fromPath, string toPath, CancellationToken cancellationToken) =>
        RunAsync(async session =>
        {
            await session.RenameAsync(fromPath, toPath, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>フォルダを中身ごと削除する。</summary>
    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(async session =>
        {
            await session.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>ファイルを受け取る（消す前に控えを取るときなど）。<paramref name="destination"/> の今の位置から書く。</summary>
    public Task DownloadAsync(string path, Stream destination, CancellationToken cancellationToken) =>
        RunAsync(async session =>
        {
            await session.DownloadAsync(path, destination, 0, null, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>属性（パーミッション）を変える。</summary>
    public Task SetPermissionsAsync(string path, int mode, CancellationToken cancellationToken) =>
        RunAsync(async session =>
        {
            await session.SetPermissionsAsync(path, mode, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// 何もしていない間に呼ぶ。接続が生きていれば true。切れていたら捨てて、次の操作でつなぎ直す。
    /// まだ接続していなければ何もしない。
    /// </summary>
    public async Task<bool> KeepAliveAsync(CancellationToken cancellationToken)
    {
        // 操作中なら、それ自体が接続を保っているので待たない。
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return true;
        try
        {
            if (_session is null)
                return true;
            try
            {
                await _session.KeepAliveAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await DropSessionAsync().ConfigureAwait(false);
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DropSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    // 一覧以外の操作。同じ操作を自動でやり直すと二重に実行されることがあるので、やり直さない。
    // 切れた接続は捨てておき、失敗はそのまま知らせる。
    private async Task<T> RunAsync<T>(Func<IRemoteSession, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(session).ConfigureAwait(false);
            }
            catch (RemoteOperationException ex)
            {
                await DropBrokenSessionAsync(ex.Failure).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var failure = FailureClassifier.FromException(ex);
                await DropBrokenSessionAsync(failure).ConfigureAwait(false);
                throw new RemoteOperationException(failure, ex.Message, ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IRemoteSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is { IsConnected: true })
            return _session;

        await DropSessionAsync().ConfigureAwait(false);
        var session = _sessionFactory.Create();
        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
            throw;
        }
        _session = session;
        return session;
    }

    private async Task DropBrokenSessionAsync(TransferFailure failure)
    {
        if (failure.IsTransient || failure.Reason == FailureReason.Unknown || _session is not { IsConnected: true })
            await DropSessionAsync().ConfigureAwait(false);
    }

    private async Task DropSessionAsync()
    {
        var session = _session;
        _session = null;
        await DisposeQuietlyAsync(session).ConfigureAwait(false);
    }

    private static async Task DisposeQuietlyAsync(IRemoteSession? session)
    {
        if (session is null)
            return;
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 切れた接続の後片付けの失敗は無視する。
        }
    }
}
