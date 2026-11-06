using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Hakobune.Core.Tests.Integration;

/// <summary>
/// サーバーとのあいだに入る中継。<see cref="DropAll"/> で、つながっている接続をすべて
/// いきなり切る（回線が切れたのと同じ状態を作る）。
/// </summary>
internal sealed class FlakyTcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Upstream)> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _nextId;
    private int _dropped;

    public FlakyTcpProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>これまでに切った接続の数。</summary>
    public int DroppedConnections => Volatile.Read(ref _dropped);

    /// <summary>いまつながっている接続を、すべていきなり切る。</summary>
    public void DropAll()
    {
        foreach (var id in _connections.Keys)
        {
            if (_connections.TryRemove(id, out var pair))
            {
                Abort(pair.Client);
                Abort(pair.Upstream);
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropAll();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
        }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = RelayAsync(client);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(_targetHost, _targetPort, _stop.Token);
        }
        catch (Exception)
        {
            Abort(client);
            Abort(upstream);
            return;
        }

        var id = Interlocked.Increment(ref _nextId);
        _connections[id] = (client, upstream);
        try
        {
            var toServer = PumpAsync(client.GetStream(), upstream.GetStream());
            var toClient = PumpAsync(upstream.GetStream(), client.GetStream());
            await Task.WhenAny(toServer, toClient);
        }
        catch (Exception)
        {
        }
        finally
        {
            _connections.TryRemove(id, out _);
            Abort(client);
            Abort(upstream);
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, _stop.Token)) > 0)
                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
        }
        catch (Exception)
        {
            // 切られた側の読み書きは失敗する。それで構わない。
        }
    }

    // 相手に RST を送って、あいさつなしで切る。
    private static void Abort(TcpClient client)
    {
        try
        {
            client.Client.LingerState = new LingerOption(true, 0);
        }
        catch (Exception)
        {
        }
        try
        {
            client.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
