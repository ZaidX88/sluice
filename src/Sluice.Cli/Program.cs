using System.Globalization;
using System.Net;
using Sluice.Core.Checker;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;
using Sluice.Core.Tunnels;

const string Usage = """
    Sluice.Cli [profile.sluice|.ppx] [options]  run the routing engine
      --socks 127.0.0.1:1080      local SOCKS5 listener address
      --intercept                 capture all applications' TCP traffic with WinDivert (needs Administrator)
      --log errors|normal|verbose|debug           on-screen log level (default normal)
      --log-file DIR [--log-file-level LEVEL]     also write a daily log file in DIR (default level verbose)
      --duration N                stop cleanly after N seconds

    Sluice.Cli check <profile.sluice|.ppx | share link> [options]
                                  test tunnels like Proxifier's Proxy Checker
      share link: vless:// vmess:// trojan:// ss:// hysteria2:// tuic:// ssh://
                  socks5:// socks4:// socks4a:// https:// http://  (scheme://[user:pass@]host:port)
      --target host:port          host reached through the proxy (default www.google.com:80)
      --no-page                   do not load a web page through the proxy
      --samples N                 latency samples (default 3)
      --timeout SECONDS           per-test timeout (default 10)

    Sluice.Cli import <profile.sluice> <share link | links.txt | --subscription URL [--name NAME]>
                                  add tunnels from share links, a file of links, or a subscription
    Sluice.Cli update <profile.sluice>
                                  refresh every subscription in the profile
    """;

if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

return args.Length > 0 ? args[0] switch
{
    "check" => await RunCheckAsync(args[1..]),
    "import" => await RunImportAsync(args[1..]),
    "update" => await RunUpdateAsync(args[1..]),
    _ => await RunEngineAsync(args),
} : await RunEngineAsync(args);

// ======================================================================================
// run
// ======================================================================================

static async Task<int> RunEngineAsync(string[] args)
{
    string? profilePath = null, logDir = null;
    var socksBind = new IPEndPoint(IPAddress.Loopback, 1080);
    var screenLevel = LogLevel.Normal;
    var fileLevel = LogLevel.Verbose;
    var intercept = false;
    TimeSpan? duration = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--intercept": intercept = true; break;
            case "--duration" when i + 1 < args.Length:
                duration = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture)); break;
            case "--socks" when i + 1 < args.Length: socksBind = IPEndPoint.Parse(args[++i]); break;
            case "--log" when i + 1 < args.Length: screenLevel = ParseLevel(args[++i]); break;
            case "--log-file" when i + 1 < args.Length: logDir = args[++i]; break;
            case "--log-file-level" when i + 1 < args.Length: fileLevel = ParseLevel(args[++i]); break;
            default:
                if (args[i].StartsWith("--")) { Console.Error.WriteLine($"Unknown option {args[i]}\n\n{Usage}"); return 1; }
                profilePath = args[i];
                break;
        }
    }

    if (!TryLoadProfile(profilePath, out var profile)) return 1;

    var tracker = new ConnectionTracker { Level = screenLevel };
    var gate = new object();
    tracker.Log += (lvl, msg) =>
    {
        if (lvl > screenLevel) return;
        lock (gate)
        {
            Console.ForegroundColor = lvl == LogLevel.Error ? ConsoleColor.Red : lvl >= LogLevel.Verbose ? ConsoleColor.DarkGray : ConsoleColor.Gray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            Console.ResetColor();
        }
    };
    using var fileLog = logDir is null ? null : new FileLog(tracker, logDir, fileLevel);
    var stats = new Statistics(tracker);

    await using var engine = new RoutingEngine(profile!, tracker);
    engine.Reload(profile!);

    await using var socks = new Socks5Inbound(engine, socksBind);
    socks.Start();

    Interceptor? interceptor = null;
    if (intercept)
    {
        interceptor = new Interceptor(engine) { DiagnosticPackets = screenLevel == LogLevel.Debug ? 20 : 0 };
        try
        {
            interceptor.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Interception unavailable: {ex.Message}");
            await interceptor.DisposeAsync();
            return 2;
        }
    }

    Console.WriteLine($"Profile: {profilePath ?? "(built-in default)"} | SOCKS5 on {socks.LocalEndPoint} | " +
                      $"interception {(intercept ? "ON" : "off")}" +
                      (fileLog is null ? "" : $" | log file in {Path.GetFullPath(logDir!)}") + " | Ctrl+C to stop");

    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
    if (duration is { } d) _ = Task.Delay(d).ContinueWith(_ => stop.TrySetResult());
    await stop.Task;

    // Close open connections first, so the log and statistics are complete.
    tracker.DisconnectAll();
    if (!await tracker.WaitIdleAsync(TimeSpan.FromSeconds(3)))
        Console.Error.WriteLine($"{tracker.Active.Count} connection(s) did not close in time.");
    if (interceptor is not null) await interceptor.DisposeAsync();

    Console.WriteLine($"\nStopping. Total sent {Bytes(tracker.TotalSent)}, received {Bytes(tracker.TotalReceived)}.");
    PrintStats(stats.Snapshot(StatisticsGrouping.Application), "Application");
    PrintStats(stats.Snapshot(StatisticsGrouping.Target), "Target");
    return 0;
}

static void PrintStats(IReadOnlyList<StatisticsRow> rows, string title)
{
    if (rows.Count == 0) return;
    Console.WriteLine($"\n{title,-34} {"conns",6} {"failed",7} {"sent",10} {"received",10}");
    foreach (var r in rows.Take(15))
        Console.WriteLine($"{Trim(r.Key, 34),-34} {r.Connections,6} {r.Failed,7} {Bytes(r.BytesSent),10} {Bytes(r.BytesReceived),10}");
    if (rows.Count > 15) Console.WriteLine($"... and {rows.Count - 15} more");
}

// ======================================================================================
// check
// ======================================================================================

static async Task<int> RunCheckAsync(string[] args)
{
    string? subject = null;
    var settings = new CheckSettings();

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--target" when i + 1 < args.Length:
                var ep = args[++i];
                var colon = ep.LastIndexOf(':');
                settings = colon > 0
                    ? settings with { TargetHost = ep[..colon].Trim('[', ']'), TargetPort = int.Parse(ep[(colon + 1)..], CultureInfo.InvariantCulture) }
                    : settings with { TargetHost = ep };
                break;
            case "--no-page": settings = settings with { LoadWebPage = false }; break;
            case "--samples" when i + 1 < args.Length: settings = settings with { LatencySamples = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
            case "--timeout" when i + 1 < args.Length:
                settings = settings with { Timeout = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture)) }; break;
            default:
                if (args[i].StartsWith("--")) { Console.Error.WriteLine($"Unknown option {args[i]}\n\n{Usage}"); return 1; }
                subject = args[i];
                break;
        }
    }
    if (subject is null) { Console.Error.WriteLine(Usage); return 1; }

    List<Tunnel> proxies;
    if (subject.Contains("://"))
    {
        var link = ShareLinks.Parse(subject);
        if (!link.Ok) { Console.Error.WriteLine($"Cannot use this link: {link.Error}"); return 1; }
        proxies = [link.Tunnel!];
    }
    else
    {
        if (!TryLoadProfile(subject, out var profile)) return 1;
        proxies = profile!.Tunnels;
        if (proxies.Count == 0) { Console.WriteLine("The profile has no tunnels."); return 0; }
    }

    Console.WriteLine($"Target: {settings.TargetHost}:{settings.TargetPort}{(settings.LoadWebPage ? " (loading a web page)" : "")}\n");
    var allPassed = true;
    await using var backends = new Sluice.Core.Tunnels.TunnelBackends(line => Console.WriteLine($"  · {line}"));
    foreach (var proxy in proxies)
    {
        Console.WriteLine($"{proxy.Type.DisplayName()} {proxy.Address}:{proxy.Port}" +
                          (string.IsNullOrEmpty(proxy.Username) ? "" : $" (user {proxy.Username})") +
                          (proxy.Label is { } label ? $"  \"{label}\"" : ""));
        var progress = new SyncProgress<CheckStep>(s =>
        {
            var (mark, color) = s.Status switch
            {
                CheckStatus.Passed => ("OK  ", ConsoleColor.Green),
                CheckStatus.Failed => ("FAIL", ConsoleColor.Red),
                _ => ("--  ", ConsoleColor.DarkGray),
            };
            Console.Write("  ");
            Console.ForegroundColor = color;
            Console.Write(mark);
            Console.ResetColor();
            var time = s.Elapsed is { } t && s.Number != 3 ? $" [{t.TotalMilliseconds:F0} ms]" : "";
            Console.WriteLine($" Test {s.Number}: {s.Name}{time}\n        {s.Detail}");
        });
        var report = await TunnelChecker.CheckAsync(proxy, settings, progress, backends: backends);
        allPassed &= report.Passed;
        Console.WriteLine(report.Passed ? "  => Tunnel is working\n" : "  => Testing failed\n");
    }
    return allPassed ? 0 : 3;
}

// ======================================================================================
// import / update
// ======================================================================================

static async Task<int> RunImportAsync(string[] args)
{
    string? profilePath = null, source = null, subscription = null, name = null;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--subscription" when i + 1 < args.Length: subscription = args[++i]; break;
            case "--name" when i + 1 < args.Length: name = args[++i]; break;
            default:
                if (args[i].StartsWith("--")) { Console.Error.WriteLine($"Unknown option {args[i]}\n\n{Usage}"); return 1; }
                if (profilePath is null) profilePath = args[i]; else source = args[i];
                break;
        }
    }
    if (profilePath is null || (source is null) == (subscription is null)) { Console.Error.WriteLine(Usage); return 1; }
    if (ProfileFile.IsProxifier(profilePath)) { Console.Error.WriteLine("Import into a .sluice profile (Proxifier profiles cannot hold these tunnels)."); return 1; }
    if (!TryLoadProfile(File.Exists(profilePath) ? profilePath : null, out var profile)) return 1;

    ImportResult result;
    if (subscription is not null)
    {
        Subscription sub;
        try { sub = Subscriptions.AddSubscription(profile!, subscription, name); }
        catch (FormatException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        try { result = await Subscriptions.UpdateAsync(profile!, sub); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
    else
    {
        var text = source!.Contains("://") ? source : File.ReadAllText(source);
        result = Subscriptions.ImportLinks(profile!, text);
    }
    PrintImport(result);
    if (result.Added.Count + result.Updated.Count + result.Removed.Count > 0) SluiceProfileSerializer.Save(profile!, profilePath);
    return result.Added.Count + result.Updated.Count + result.Unchanged.Count > 0 ? 0 : 2;
}

static async Task<int> RunUpdateAsync(string[] args)
{
    if (args.Length != 1) { Console.Error.WriteLine(Usage); return 1; }
    if (!TryLoadProfile(args[0], out var profile)) return 1;
    if (profile!.Subscriptions.Count == 0) { Console.WriteLine("The profile has no subscriptions."); return 0; }
    var failed = 0;
    foreach (var sub in profile.Subscriptions.ToList())
    {
        Console.WriteLine($"{sub.Name} ({sub.Url})");
        try
        {
            PrintImport(await Subscriptions.UpdateAsync(profile, sub));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            failed++;
            Console.WriteLine($"  {ex.Message} (tunnels kept as they were)");
        }
    }
    SluiceProfileSerializer.Save(profile, args[0]);
    return failed == 0 ? 0 : 2;
}

static void PrintImport(ImportResult r)
{
    foreach (var t in r.Added) Console.WriteLine($"  + {t.Type.DisplayName(),-12} {t}");
    foreach (var t in r.Updated) Console.WriteLine($"  ~ {t.Type.DisplayName(),-12} {t}");
    foreach (var t in r.Removed) Console.WriteLine($"  - {t.Type.DisplayName(),-12} {t}");
    foreach (var w in r.Warnings) Console.WriteLine($"  ! {w}");
    Console.WriteLine($"  {r.Summary}");
}

static bool TryLoadProfile(string? path, out Profile? profile)
{
    try
    {
        profile = path is null ? Profile.CreateDefault() : ProfileFile.Load(path, PromptMasterPassword);
        return true;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Cannot load profile: {ex.Message}");
    }
    profile = null;
    return false;
}

static LogLevel ParseLevel(string s) => s.ToLowerInvariant() switch
{
    "errors" or "error" => LogLevel.Error,
    "verbose" => LogLevel.Verbose,
    "debug" => LogLevel.Debug,
    _ => LogLevel.Normal,
};

static string Bytes(long b) => b switch
{
    < 1024 => $"{b} B",
    < 1024 * 1024 => $"{b / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{b / 1048576.0:F1} MB",
    _ => $"{b / 1073741824.0:F2} GB",
};

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

// Reads the profile master password without echoing it.
static string? PromptMasterPassword(string profileName, bool confirmNew)
{
    if (Console.IsInputRedirected)
        return Console.ReadLine();
    Console.Write($"Master password for profile '{profileName}': ");
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return null; }
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

/// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; posts to the thread pool and can reorder output).</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
