using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Engine;
using Sluice.Core.Proxy;

namespace Sluice.Core.Inbound;

/// <summary>
/// Local SOCKS5 server (no auth, CONNECT only). Applications configured to use it
/// (or curl --socks5-hostname) go through the rule engine like intercepted traffic.
/// </summary>
public sealed class Socks5Inbound(ProxyEngine engine, IPEndPoint bind) : InboundListener(engine, bind)
{
    protected override async Task ServeAsync(Socket client, CancellationToken ct)
    {
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
        var hct = handshakeCts.Token;

        var head = await ReadAsync(client, 2, hct);
        if (head[0] != 5) throw new IOException("Not SOCKS5");
        var methods = await ReadAsync(client, head[1], hct);
        if (!methods.Contains((byte)0))
        {
            await client.SendAsync(new byte[] { 5, 0xFF }, hct);
            client.Dispose();
            return;
        }
        await client.SendAsync(new byte[] { 5, 0 }, hct);

        var req = await ReadAsync(client, 4, hct);
        string host = req[3] switch
        {
            1 => new IPAddress(await ReadAsync(client, 4, hct)).ToString(),
            4 => new IPAddress(await ReadAsync(client, 16, hct)).ToString(),
            3 => Encoding.ASCII.GetString(await ReadAsync(client, (await ReadAsync(client, 1, hct))[0], hct)),
            _ => throw new IOException("Bad SOCKS5 address type"),
        };
        var pb = await ReadAsync(client, 2, hct);
        var port = (pb[0] << 8) | pb[1];

        if (req[1] != 1) // only CONNECT
        {
            await client.SendAsync(Reply(7), hct);
            client.Dispose();
            return;
        }

        var (pid, path) = IdentifyClient(client);
        await Engine.HandleAsync(client, new InboundRequest(host, port, pid, path), new Socks5Reply(client), ct);
    }

    private static byte[] Reply(byte code) => [5, code, 0, 1, 0, 0, 0, 0, 0, 0];

    private sealed class Socks5Reply(Socket s) : IInboundReply
    {
        public async Task SuccessAsync(CancellationToken ct) => await s.SendAsync(Reply(0), ct);

        public async Task FailAsync(Exception error, CancellationToken ct)
        {
            byte code = error switch
            {
                ConnectionBlockedException => 2,
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } => 5,
                SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.HostUnreachable } => 4,
                SocketException { SocketErrorCode: SocketError.NetworkUnreachable } => 3,
                TimeoutException => 6,
                _ => 1,
            };
            await s.SendAsync(Reply(code), ct);
        }
    }

    private static async Task<byte[]> ReadAsync(Socket s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var got = 0;
        while (got < n)
        {
            var r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            if (r == 0) throw new EndOfStreamException();
            got += r;
        }
        return buf;
    }
}
