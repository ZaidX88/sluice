using System.Net;
using System.Net.Sockets;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;

namespace Sluice.Platform.Linux.Capture;

/// <summary>
/// The transparent relay on 127.0.0.1:P and [::1]:P, one port P for both. nftables' <c>redirect</c> of a locally
/// generated connection goes to the loopback address of its family and keeps the given port, so the relay never needs
/// to be reachable from the network (it used to listen on <c>[::]</c>, every interface).
/// </summary>
internal sealed class LoopbackRelay : IAsyncDisposable
{
    private LoopbackRelay(TransparentInbound v4, TransparentInbound? v6)
    {
        V4 = v4;
        V6 = v6;
    }

    public TransparentInbound V4 { get; }

    /// <summary>Null on a host without IPv6.</summary>
    public TransparentInbound? V6 { get; }

    public int Port => V4.LocalEndPoint.Port;

    public static LoopbackRelay Bind(RoutingEngine engine, IRedirectTable table)
    {
        for (var attempt = 0; ; attempt++)
        {
            var v4 = new TransparentInbound(engine, new IPEndPoint(IPAddress.Loopback, 0), table);
            try
            {
                var v6 = new TransparentInbound(engine, new IPEndPoint(IPAddress.IPv6Loopback, v4.LocalEndPoint.Port), table);
                return new LoopbackRelay(v4, v6);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable)
            {
                return new LoopbackRelay(v4, null);     // IPv6 missing or disabled: IPv4 connections only
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 20)
            {
                v4.DisposeAsync().AsTask().GetAwaiter().GetResult();   // not started: completes at once. Port taken on ::1, try another
            }
        }
    }

    public void Start()
    {
        V4.Start();
        V6?.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await V4.DisposeAsync();
        if (V6 is not null) await V6.DisposeAsync();
    }
}
