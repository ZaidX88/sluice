using System.Net;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Interception;
using Sluice.Core.Profiles;

// Usage: Sluice.Cli [profile.ppx] [--socks 127.0.0.1:1080] [--intercept] [--log normal|verbose|debug|errors]
string? profilePath = null;
var socksBind = new IPEndPoint(IPAddress.Loopback, 1080);
var level = LogLevel.Normal;
var intercept = false;
TimeSpan? duration = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--intercept":
            intercept = true;
            break;
        case "--duration" when i + 1 < args.Length:
            duration = TimeSpan.FromSeconds(double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "--socks" when i + 1 < args.Length:
            socksBind = IPEndPoint.Parse(args[++i]);
            break;
        case "--log" when i + 1 < args.Length:
            level = args[++i].ToLowerInvariant() switch
            {
                "errors" => LogLevel.Error,
                "verbose" => LogLevel.Verbose,
                "debug" => LogLevel.Debug,
                _ => LogLevel.Normal,
            };
            break;
        case "-h" or "--help":
            Console.WriteLine("Sluice.Cli [profile.ppx] [--socks 127.0.0.1:1080] [--intercept] [--log errors|normal|verbose|debug]");
            Console.WriteLine("  --intercept  capture all applications' TCP traffic with WinDivert (needs Administrator)");
            Console.WriteLine("  --duration N stop cleanly after N seconds");
            return 0;
        default:
            profilePath = args[i];
            break;
    }
}

Profile profile;
try
{
    profile = profilePath is null ? Profile.CreateDefault() : PpxSerializer.Load(profilePath, PromptMasterPassword);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot load profile: {ex.Message}");
    return 1;
}

var tracker = new ConnectionTracker { Level = level };
var gate = new object();
tracker.Log += (lvl, msg) =>
{
    lock (gate)
    {
        Console.ForegroundColor = lvl == LogLevel.Error ? ConsoleColor.Red : lvl >= LogLevel.Verbose ? ConsoleColor.DarkGray : ConsoleColor.Gray;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        Console.ResetColor();
    }
};

var engine = new ProxyEngine(profile, tracker);
engine.Reload(profile);

await using var socks = new Socks5Inbound(engine, socksBind);
socks.Start();

Interceptor? interceptor = null;
if (intercept)
{
    interceptor = new Interceptor(engine) { DiagnosticPackets = level == LogLevel.Debug ? 20 : 0 };
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
                  $"interception {(intercept ? "ON" : "off")} | Ctrl+C to stop");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
if (duration is { } d) _ = Task.Delay(d).ContinueWith(_ => stop.TrySetResult());
await stop.Task;

if (interceptor is not null) await interceptor.DisposeAsync();

Console.WriteLine($"Stopping. Total sent {tracker.TotalSent:N0} B, received {tracker.TotalReceived:N0} B.");
return 0;

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
