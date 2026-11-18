using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Dns;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Tests;

/// <summary>UDP echo on 127.0.0.1.</summary>
public sealed class UdpEchoServer : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public int Received;

    public UdpEchoServer() => _ = Loop();

    private async Task Loop()
    {
        try
        {
            while (true)
            {
                var r = await _udp.ReceiveAsync();
                Interlocked.Increment(ref Received);
                await _udp.SendAsync(r.Buffer, r.RemoteEndPoint);
            }
        }
        catch { /* disposed */ }
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>
/// Minimal SOCKS5 server supporting only UDP ASSOCIATE (no auth). Hostnames are resolved through
/// <see cref="Hosts"/> (so tests can use names that only exist here).
/// </summary>
public sealed class Socks5UdpServer : LoopbackServer
{
    public Dictionary<string, IPAddress> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Associations;
    public string? LastTargetHost;

    protected override async Task HandleAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[512];
        await s.ReadExactlyAsync(buf.AsMemory(0, 2), ct);
        await s.ReadExactlyAsync(buf.AsMemory(0, buf[1]), ct);
        await s.WriteAsync(new byte[] { 5, 0 }, ct);
        await s.ReadExactlyAsync(buf.AsMemory(0, 4), ct);
        var cmd = buf[1];
        var skip = buf[3] switch { 1 => 4, 4 => 16, _ => -1 };
        if (skip < 0) { await s.ReadExactlyAsync(buf.AsMemory(0, 1), ct); skip = buf[0]; }
        await s.ReadExactlyAsync(buf.AsMemory(0, skip + 2), ct);
        if (cmd != 3)
        {
            await s.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        // Reply "0.0.0.0": the client must use the address it connected to.
        await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, (byte)(port >> 8), (byte)port }, ct);
        Interlocked.Increment(ref Associations);

        IPEndPoint? client = null;
        var pump = Task.Run(async () =>
        {
            var d = new byte[65536];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveFromAsync(d, SocketFlags.None, any, ct);
                var from = (IPEndPoint)r.RemoteEndPoint;
                if (client is null || from.Equals(client))
                {
                    client = from;
                    if (!Socks5UdpAssociation.TryParse(d.AsSpan(0, r.ReceivedBytes), out var host, out var p, out var off)) continue;
                    LastTargetHost = host;
                    var ip = IPAddress.TryParse(host, out var lit) ? lit : Hosts[host];
                    await udp.SendToAsync(d.AsMemory(off, r.ReceivedBytes - off), SocketFlags.None, new IPEndPoint(ip, p), ct);
                }
                else
                {
                    var reply = new byte[10 + r.ReceivedBytes];
                    reply[3] = 1;
                    from.Address.GetAddressBytes().CopyTo(reply, 4);
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), (ushort)from.Port);
                    d.AsSpan(0, r.ReceivedBytes).CopyTo(reply.AsSpan(10));
                    await udp.SendToAsync(reply, SocketFlags.None, client, ct);
                }
            }
        }, ct);
        // The association lives as long as the control connection.
        try { while (await s.ReadAsync(buf, ct) > 0) { } } catch { }
        udp.Dispose();
        try { await pump; } catch { }
    }
}
