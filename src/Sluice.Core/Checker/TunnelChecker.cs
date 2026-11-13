using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sluice.Core.Profiles;
using Sluice.Core.Proxy;

namespace Sluice.Core.Checker;

public sealed record CheckSettings
{
    /// <summary>Host reached through the proxy in tests 2 and 3.</summary>
    public string TargetHost { get; init; } = "www.google.com";
    public int TargetPort { get; init; } = 80;
    /// <summary>Send an HTTP GET through the tunnel and expect an HTTP reply (needed to really prove HTTP proxies work).</summary>
    public bool LoadWebPage { get; init; } = true;
    public int LatencySamples { get; init; } = 3;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

public enum CheckStatus { Passed, Failed, Skipped }

public sealed record CheckStep(int Number, string Name, CheckStatus Status, string Detail, TimeSpan? Elapsed = null);

public sealed record CheckReport(Tunnel Proxy, IReadOnlyList<CheckStep> Steps)
{
    public bool Passed => Steps.All(s => s.Status != CheckStatus.Failed);
    public TimeSpan? Latency => Steps.FirstOrDefault(s => s.Number == 3 && s.Status == CheckStatus.Passed)?.Elapsed;
}

/// <summary>
/// Equivalent of Proxifier's TunnelChecker.exe:
///   Test 1: Connection to the Proxy Server
///   Test 2: Connection through the Proxy Server (optionally loading a web page)
///   Test 3: Proxy Server latency
/// </summary>
public static class TunnelChecker
{
    /// <param name="backends">Runs encrypted tunnels (SSH, …); required to check those.</param>
    public static async Task<CheckReport> CheckAsync(Tunnel proxy, CheckSettings? settings = null,
        IProgress<CheckStep>? progress = null, CancellationToken ct = default, Tunnels.TunnelBackends? backends = null)
    {
        settings ??= new CheckSettings();
        var steps = new List<CheckStep>();
        var original = proxy;

        void Add(CheckStep s)
        {
            steps.Add(s);
            progress?.Report(s);
        }

        // ---- Test 1: reach the tunnel server (for SSH: transport, host key and login).
        Result t1;
        if (Tunnels.TunnelBackends.Handles(proxy.Type))
        {
            Tunnel? local = null;
            t1 = await Timed(async c =>
            {
                if (backends is null) throw new ProxyException($"{proxy.Type.DisplayName()} tunnels cannot be checked here");
                local = await backends.AsLocalSocksAsync(original, c);
                return $"{original.Type.DisplayName()} session to {original.Address}:{original.Port} established" +
                       (string.IsNullOrEmpty(original.Username) ? "" : $" as {original.Username}");
            }, TimeSpan.FromSeconds(Math.Max(settings.Timeout.TotalSeconds, 30)), ct);
            if (local is not null) proxy = local;   // tests 2 and 3 run through the session
        }
        else
        {
            t1 = await Timed(async c =>
            {
                using var tcp = await ConnectAsync(proxy.Address, proxy.Port, c);
                return $"Connected to {proxy.Address}:{proxy.Port} ({tcp.Client.RemoteEndPoint})";
            }, settings.Timeout, ct);
        }
        Add(new CheckStep(1, "Connection to the tunnel server", t1.Ok ? CheckStatus.Passed : CheckStatus.Failed, t1.Detail, t1.Elapsed));

        if (!t1.Ok)
        {
            Add(new CheckStep(2, "Connection through the tunnel", CheckStatus.Skipped, "Skipped: the tunnel server is not reachable"));
            Add(new CheckStep(3, "Tunnel latency", CheckStatus.Skipped, "Skipped: the tunnel server is not reachable"));
            return new CheckReport(original, steps);
        }

        // ---- Test 2: tunnel to the target (+ optional page load).
        var target = $"{settings.TargetHost}:{settings.TargetPort}";
        var t2 = await Timed(async c =>
        {
            using var tcp = await ConnectAsync(proxy.Address, proxy.Port, c);
            var s = tcp.GetStream();
            await ProxyHandshake.ConnectAsync(s, proxy, settings.TargetHost, settings.TargetPort, c);
            var detail = $"Connected to {target} through the tunnel" + AuthNote(original);
            if (settings.LoadWebPage)
                detail += $"; web server replied \"{await FetchStatusLineAsync(s, settings.TargetHost, c)}\"";
            return detail;
        }, settings.Timeout, ct);
        Add(new CheckStep(2, "Connection through the tunnel", t2.Ok ? CheckStatus.Passed : CheckStatus.Failed, t2.Detail, t2.Elapsed));

        if (!t2.Ok)
        {
            Add(new CheckStep(3, "Tunnel latency", CheckStatus.Skipped, "Skipped: tunnelling failed"));
            return new CheckReport(original, steps);
        }

        // ---- Test 3: average time for a full exchange with the target through the proxy.
        // Many proxies confirm the handshake before they have actually reached the target, so when
        // page loading is on we time until the web server's first reply line, not just the handshake.
        var samples = new List<TimeSpan>();
        string? error = null;
        for (var i = 0; i < Math.Max(1, settings.LatencySamples) && error is null; i++)
        {
            var r = await Timed(async c =>
            {
                using var tcp = await ConnectAsync(proxy.Address, proxy.Port, c);
                var s = tcp.GetStream();
                await ProxyHandshake.ConnectAsync(s, proxy, settings.TargetHost, settings.TargetPort, c);
                if (settings.LoadWebPage) await FetchStatusLineAsync(s, settings.TargetHost, c);
                return "";
            }, settings.Timeout, ct);
            if (r.Ok) samples.Add(r.Elapsed); else error = r.Detail;
        }
        if (error is not null)
            Add(new CheckStep(3, "Tunnel latency", CheckStatus.Failed, error));
        else
        {
            var avg = TimeSpan.FromTicks((long)samples.Average(x => x.Ticks));
            var what = settings.LoadWebPage ? "request round trip to the target" : "tunnel setup (handshake only)";
            Add(new CheckStep(3, "Tunnel latency", CheckStatus.Passed,
                $"{avg.TotalMilliseconds:F0} ms average {what} over {samples.Count} connections " +
                $"(min {samples.Min().TotalMilliseconds:F0} ms, max {samples.Max().TotalMilliseconds:F0} ms)", avg));
        }

        return new CheckReport(original, steps);
    }

    private static string AuthNote(Tunnel p) =>
        string.IsNullOrEmpty(p.Username) ? "" : "; authentication was successful";

    private static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        tcp.Client.DualMode = true;
        try
        {
            if (IPAddress.TryParse(host, out var ip)) await tcp.ConnectAsync(ip, port, ct);
            else await tcp.ConnectAsync(host, port, ct);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>GET / and return the status line; throws if the reply is not HTTP.</summary>
    private static async Task<string> FetchStatusLineAsync(Stream s, string host, CancellationToken ct)
    {
        var req = $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: Sluice-TunnelChecker\r\nAccept: */*\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(req), ct);
        var buf = new byte[512];
        var got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) break;
            got += n;
            if (Array.IndexOf(buf, (byte)'\n', 0, got) >= 0) break;
        }
        var line = Encoding.ASCII.GetString(buf, 0, got).Split('\n')[0].TrimEnd('\r');
        if (!line.StartsWith("HTTP/", StringComparison.Ordinal))
            throw new IOException(got == 0
                ? "The target host closed the connection without replying. Please make sure that the target host is a Web Server."
                : "The reply from the target host does not look like a usual Web Server reply.");
        return line;
    }

    internal readonly record struct Result(bool Ok, string Detail, TimeSpan Elapsed);

    private static async Task<Result> Timed(Func<CancellationToken, Task<string>> action, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await action(cts.Token);
            return new Result(true, detail, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Result(false, $"Timed out after {timeout.TotalSeconds:F0} s", sw.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ProxyException)
        {
            return new Result(false, Explain(ex), sw.Elapsed);
        }
    }

    /// <summary>Plain-language explanation, in the spirit of TunnelChecker's messages.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            "No connection could be made because the target computer actively refused it. Check the tunnel server's address and port.",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } =>
            "The tunnel server's hostname could not be resolved.",
        SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } =>
            "The tunnel server is unreachable.",
        ProxyException pe when pe.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) || pe.Message.Contains("407") =>
            $"Authentication on the tunnel server failed ({pe.Message}).",
        ProxyException pe when pe.Message.Contains("not allowed by ruleset") =>
            "Connection to the target is denied by the tunnel server's access rules.",
        ProxyException pe when pe.Message.Contains("SOCKS4 needs an IP") =>
            "Please enable the SOCKS 4A extension or specify the IP address of the target host.",
        _ => ex.Message,
    };
}
