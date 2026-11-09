using System.Net;
using System.Net.Sockets;
using Sluice.Core.Engine;
using Sluice.Core.Platform;

namespace Sluice.Core.Inbound;

/// <summary>Accept loop shared by all inbound listeners. Registers itself with the engine's loop guard.</summary>
public abstract class InboundListener : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    protected ProxyEngine Engine { get; }
    public IPEndPoint LocalEndPoint { get; }

    protected InboundListener(ProxyEngine engine, IPEndPoint bind)
    {
        Engine = engine;
        _socket = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (bind.AddressFamily == AddressFamily.InterNetworkV6) _socket.DualMode = true;
        _socket.Bind(bind);
        _socket.Listen(512);
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
    }

    public void Start()
    {
        Engine.RegisterOwnEndpoint(LocalEndPoint);
        _loop = Task.Run(AcceptLoopAsync);
        Engine.Tracker.Write(LogLevel.Normal, $"{GetType().Name} listening on {LocalEndPoint}");
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await _socket.AcceptAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                Engine.Tracker.Write(LogLevel.Error, $"Accept failed: {ex.Message}");
                continue;
            }
            client.NoDelay = true;
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client, ct); }
                catch (Exception ex)
                {
                    Engine.Tracker.Write(LogLevel.Debug, $"{GetType().Name}: {ex.Message}");
                    client.Dispose();
                }
            }, ct);
        }
    }

    /// <summary>Handle one accepted socket; ownership passes to the implementation.</summary>
    protected abstract Task ServeAsync(Socket client, CancellationToken ct);

    /// <summary>PID + exe of the application on the other end of an accepted loopback socket.</summary>
    protected static (int Pid, string? Path) IdentifyClient(Socket client)
    {
        try
        {
            var pid = ProcessLookup.FindTcpOwner((IPEndPoint)client.RemoteEndPoint!, (IPEndPoint)client.LocalEndPoint!);
            return (pid, pid == 0 ? null : ProcessLookup.GetProcessPath(pid));
        }
        catch
        {
            return (0, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _socket.Dispose();
        Engine.UnregisterOwnEndpoint(LocalEndPoint);
        if (_loop is not null) await _loop;
        GC.SuppressFinalize(this);
    }
}
