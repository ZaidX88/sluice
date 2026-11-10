using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Profiles;

namespace Sluice.Core.Proxy;

/// <summary>
/// A SOCKS5 UDP ASSOCIATE session (RFC 1928 §7): a TCP control connection that keeps the association
/// alive, and a UDP socket that exchanges datagrams with the server's relay, each prefixed with the
/// real destination (or source, for replies). One association can talk to any number of destinations.
/// </summary>
public sealed class Socks5UdpAssociation : IAsyncDisposable
{
    private readonly TcpClient _control;
    private readonly Socket _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string, int, ReadOnlyMemory<byte>> _onReceive;
    private volatile bool _closed;

    /// <summary>The server's UDP relay endpoint.</summary>
    public IPEndPoint Relay { get; }

    public bool IsAlive => !_closed;

    /// <summary>Raised once when the server ends the association (control connection closed).</summary>
    public event Action? Closed;

    private Socks5UdpAssociation(TcpClient control, Socket udp, IPEndPoint relay, Action<string, int, ReadOnlyMemory<byte>> onReceive)
    {
        _control = control;
        _udp = udp;
        Relay = relay;
        _onReceive = onReceive;
    }

    /// <param name="proxy">A SOCKS5 tunnel (for encrypted tunnels: the local endpoint of their session).</param>
    /// <param name="onReceive">Called for every datagram from the relay with its origin host, port and payload.</param>
    public static async Task<Socks5UdpAssociation> OpenAsync(Tunnel proxy, Action<string, int, ReadOnlyMemory<byte>> onReceive,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (proxy.Type != TunnelType.Socks5) throw new ProxyException($"{proxy.Type.DisplayName()} cannot carry UDP");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        IPAddress[] addrs = IPAddress.TryParse(proxy.Address, out var lit) ? [lit]
            : resolver is not null ? await resolver(proxy.Address, cts.Token)
            : await System.Net.Dns.GetHostAddressesAsync(proxy.Address, cts.Token);

        var control = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        control.Client.DualMode = true;
        Socket? udp = null;
        try
        {
            await control.ConnectAsync(addrs, proxy.Port, cts.Token);
            var (boundHost, boundPort) = await ProxyHandshake.Socks5RequestAsync(control.GetStream(), 0x03, "0.0.0.0", 0,
                proxy.Username, proxy.Password, cts.Token);

            // "0.0.0.0" / "::" means "the address you reached me on".
            var server = ((IPEndPoint)control.Client.RemoteEndPoint!).Address;
            if (server.IsIPv4MappedToIPv6) server = server.MapToIPv4();
            var relayIp = IPAddress.TryParse(boundHost, out var b) && !b.Equals(IPAddress.Any) && !b.Equals(IPAddress.IPv6Any) ? b : server;
            if (relayIp.IsIPv4MappedToIPv6) relayIp = relayIp.MapToIPv4();
            var relay = new IPEndPoint(relayIp, boundPort);

            udp = new Socket(relay.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(relay.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            udp.Connect(relay);   // only datagrams from the relay are accepted
            IgnoreConnectionReset(udp);

            var a = new Socks5UdpAssociation(control, udp, relay, onReceive);
            _ = a.ReceiveLoop();
            _ = a.WatchControl();
            return a;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            control.Dispose();
            udp?.Dispose();
            throw new TimeoutException($"SOCKS5 UDP associate with {proxy} timed out");
        }
        catch
        {
            control.Dispose();
            udp?.Dispose();
            throw;
        }
    }

    /// <summary>Sends one datagram to <paramref name="host"/>:<paramref name="port"/> (a hostname is resolved by the server).</summary>
    public ValueTask<int> SendAsync(string host, int port, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (_closed) throw new ObjectDisposedException(nameof(Socks5UdpAssociation));
        var header = new List<byte>(22) { 0, 0, 0 };
        ProxyHandshake.AppendSocks5Address(header, host);
        header.Add((byte)(port >> 8));
        header.Add((byte)port);
        var datagram = new byte[header.Count + payload.Length];
        header.CopyTo(datagram);
        payload.Span.CopyTo(datagram.AsSpan(header.Count));
        return _udp.SendAsync(datagram, SocketFlags.None, ct);
    }

    /// <summary>Parses a relay datagram: RSV RSV FRAG ATYP ADDR PORT DATA. Fragments are not supported (dropped).</summary>
    internal static bool TryParse(ReadOnlySpan<byte> d, out string host, out int port, out int dataOffset)
    {
        host = ""; port = 0; dataOffset = 0;
        if (d.Length < 4 || d[2] != 0) return false;
        int addrEnd;
        switch (d[3])
        {
            case 0x01:
                addrEnd = 8;
                if (d.Length < addrEnd + 2) return false;
                host = new IPAddress(d[4..8]).ToString();
                break;
            case 0x04:
                addrEnd = 20;
                if (d.Length < addrEnd + 2) return false;
                host = new IPAddress(d[4..20]).ToString();
                break;
            case 0x03:
                if (d.Length < 5) return false;
                addrEnd = 5 + d[4];
                if (d.Length < addrEnd + 2) return false;
                host = Encoding.ASCII.GetString(d[5..addrEnd]);
                break;
            default:
                return false;
        }
        port = BinaryPrimitives.ReadUInt16BigEndian(d[addrEnd..]);
        dataOffset = addrEnd + 2;
        return true;
    }

    private async Task ReceiveLoop()
    {
        var buf = new byte[65536];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await _udp.ReceiveAsync(buf, SocketFlags.None, _cts.Token);
                if (!TryParse(buf.AsSpan(0, n), out var host, out var port, out var off)) continue;
                try { _onReceive(host, port, buf.AsMemory(off, n - off)); }
                catch { /* a bad consumer must not kill the association */ }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            Close();
        }
    }

    private async Task WatchControl()
    {
        var buf = new byte[256];
        try
        {
            var s = _control.GetStream();
            while (await s.ReadAsync(buf, _cts.Token) > 0) { /* the server sends nothing; EOF = association over */ }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            Close();
        }
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        _udp.Dispose();
        _control.Dispose();
        Closed?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>Windows reports ICMP port-unreachable as WSAECONNRESET on the next receive; ignore it.</summary>
    private static void IgnoreConnectionReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try { s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { /* not critical */ }
    }
}
