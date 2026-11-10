using System.Net;
using System.Net.Sockets;
using Sluice.Core.Engine;

namespace Sluice.Core.Inbound;

/// <summary>Where an intercepted connection was originally headed, and who made it.</summary>
public sealed record OriginalDestination(IPEndPoint Destination, int ProcessId, string? Hostname = null);

/// <summary>
/// Supplied by the interception layer (WinDivert in step 3): given the client endpoint of a
/// connection that was redirected to our listener, return its original destination.
/// Endpoints passed in are normalized (IPv4-mapped IPv6 → IPv4).
/// </summary>
public interface IRedirectTable
{
    bool TryGetOriginal(IPEndPoint clientEndPoint, out OriginalDestination original);

    static IPEndPoint Normalize(IPEndPoint ep) =>
        ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;
}

/// <summary>
/// Listener for transparently redirected connections. The app thinks it is talking to the
/// real server; there is no handshake, so failures simply close the socket.
/// </summary>
public sealed class TransparentInbound(RoutingEngine engine, IPEndPoint bind, IRedirectTable table) : InboundListener(engine, bind)
{
    protected override async Task ServeAsync(Socket client, CancellationToken ct)
    {
        var remote = IRedirectTable.Normalize((IPEndPoint)client.RemoteEndPoint!);
        if (!table.TryGetOriginal(remote, out var orig))
        {
            Engine.Tracker.Write(LogLevel.Error, $"No redirect record for {remote}; dropping");
            client.Dispose();
            return;
        }

        var pid = orig.ProcessId;
        var path = pid == 0 ? null : Platform.ProcessLookup.GetProcessPath(pid);
        var host = orig.Hostname ?? orig.Destination.Address.ToString();
        await Engine.HandleAsync(client, new InboundRequest(host, orig.Destination.Port, pid, path), NoHandshake.Instance, ct);
    }

    private sealed class NoHandshake : IInboundReply
    {
        public static readonly NoHandshake Instance = new();
        public Task SuccessAsync(CancellationToken ct) => Task.CompletedTask;
        public Task FailAsync(Exception error, CancellationToken ct) => Task.CompletedTask;
    }
}
