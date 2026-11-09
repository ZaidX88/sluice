using System.Net;
using Sluice.Core.Engine;
using Sluice.Core.Inbound;
using Sluice.Core.Profiles;

// Usage: Sluice.Cli [profile.ppx] [--socks 127.0.0.1:1080] [--log normal|verbose|debug|errors]
string? profilePath = null;
var socksBind = new IPEndPoint(IPAddress.Loopback, 1080);
var level = LogLevel.Normal;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
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
            Console.WriteLine("Sluice.Cli [profile.ppx] [--socks 127.0.0.1:1080] [--log errors|normal|verbose|debug]");
            return 0;
        default:
            profilePath = args[i];
            break;
    }
}

Profile profile;
try
{
    profile = profilePath is null ? Profile.CreateDefault() : PpxSerializer.Load(profilePath);
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

Console.WriteLine($"Profile: {profilePath ?? "(built-in default)"} | SOCKS5 on {socks.LocalEndPoint} | Ctrl+C to stop");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
await stop.Task;

Console.WriteLine($"Stopping. Total sent {tracker.TotalSent:N0} B, received {tracker.TotalReceived:N0} B.");
return 0;
