using System.Net.Http.Headers;
using System.Text.Json;

namespace Sluice.Core.Updates;

/// <summary>A newer published release: its version, its GitHub page, and the package for this OS (null if none fits).</summary>
public sealed record UpdateInfo(string Version, Uri ReleasePage, Uri? Download);

/// <summary>
/// Whether a newer Sluice than this build has been released (#196, #197): one request to GitHub's public releases API.
/// No download or install; the desktop app shows a notice with the link (#198).
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "ZaidX88/sluice";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// The latest published release newer than <see cref="SluiceVersion"/>; null when this build is up to date, offline,
    /// rate-limited or on any other error (never throws, except when <paramref name="ct"/> is cancelled).
    /// Release candidates count only when <paramref name="includePreReleases"/> is set. <paramref name="log"/> gets one
    /// line when the check fails.
    /// </summary>
    public static async Task<UpdateInfo?> GetNewerAsync(bool includePreReleases, CancellationToken ct, Action<string>? log = null)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", ct);
            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"Update check: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            return PickNewer(json, SluiceVersion.Text, includePreReleases, CurrentPackageKind());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Update check failed: {ex.Message}");
            return null;
        }
    }

    // ---------------- selection (pure, tested without the network) ----------------

    /// <summary>Which package a user of this machine installs.</summary>
    public enum PackageKind { WindowsInstaller, UbuntuDeb, LinuxTarGz }

    /// <summary>
    /// From GitHub's releases JSON: the highest published (not draft) release newer than <paramref name="current"/>, with
    /// its package for <paramref name="kind"/>. Malformed JSON or versions are skipped, never thrown.
    /// </summary>
    internal static UpdateInfo? PickNewer(string json, string current, bool includePreReleases, PackageKind kind)
    {
        if (SemVersion.TryParse(current) is not { } now) return null;
        (SemVersion Version, JsonElement Release)? best = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (Bool(r, "draft")) continue;
                if (SemVersion.TryParse(Str(r, "tag_name")?.TrimStart('v', 'V')) is not { } v) continue;
                if ((Bool(r, "prerelease") || v.IsPreRelease) && !includePreReleases) continue;
                if (v.CompareTo(now) <= 0) continue;
                if (best is null || v.CompareTo(best.Value.Version) > 0) best = (v, r.Clone());
            }
        }
        catch (JsonException)
        {
            return null;
        }
        if (best is not { } b || !Uri.TryCreate(Str(b.Release, "html_url"), UriKind.Absolute, out var page)) return null;

        var assets = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        if (b.Release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
                if (Str(a, "name") is { } name && Uri.TryCreate(Str(a, "browser_download_url"), UriKind.Absolute, out var url))
                    assets[name] = url;
        return new UpdateInfo(b.Version.Text, page, PickAsset(assets, b.Version.Text, kind));
    }

    /// <summary>The release file for <paramref name="kind"/>: the names the release workflow (#74) and #67 give them.</summary>
    internal static Uri? PickAsset(IReadOnlyDictionary<string, Uri> assets, string version, PackageKind kind)
    {
        Uri? Get(string name) => assets.TryGetValue(name, out var u) ? u : null;
        return kind switch
        {
            PackageKind.WindowsInstaller => Get($"Sluice-Setup-{version}-x64.exe"),
            PackageKind.UbuntuDeb => Get($"sluice_{version}_amd64.deb") ?? Get($"sluice-{version}-linux-x64.tar.gz"),
            _ => Get($"sluice-{version}-linux-x64.tar.gz"),
        };
    }

    internal static PackageKind CurrentPackageKind()
    {
        if (OperatingSystem.IsWindows()) return PackageKind.WindowsInstaller;
        try
        {
            if (File.Exists("/etc/os-release") && IsUbuntu(File.ReadAllLines("/etc/os-release"))) return PackageKind.UbuntuDeb;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return PackageKind.LinuxTarGz;
    }

    /// <summary>Ubuntu, or a distribution based on it (ID_LIKE), from /etc/os-release's lines.</summary>
    internal static bool IsUbuntu(IEnumerable<string> osRelease) => osRelease.Any(line =>
    {
        var eq = line.IndexOf('=');
        if (eq < 0) return false;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim().Trim('"', '\'');
        return key == "ID" && value.Equals("ubuntu", StringComparison.OrdinalIgnoreCase)
            || key == "ID_LIKE" && value.Split(' ').Contains("ubuntu", StringComparer.OrdinalIgnoreCase);
    });

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sluice", SemVersion.TryParse(SluiceVersion.Text)?.Text ?? "0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}

/// <summary>
/// A Semantic Versioning 2.0 version as Sluice uses it ("1.0.0", "1.0.0-rc.2"); build metadata after "+" is ignored.
/// Order: 1.0.0-dev &lt; 1.0.0-rc.1 &lt; 1.0.0-rc.2 &lt; 1.0.0 &lt; 1.0.1.
/// </summary>
internal sealed class SemVersion : IComparable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> PreRelease { get; }
    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>"1.0.0-rc.1" (no build metadata).</summary>
    public string Text => $"{Major}.{Minor}.{Patch}" + (IsPreRelease ? "-" + string.Join('.', PreRelease) : "");

    private SemVersion(int major, int minor, int patch, IReadOnlyList<string> pre) =>
        (Major, Minor, Patch, PreRelease) = (major, minor, patch, pre);

    public static SemVersion? TryParse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var nums = core.Split('.');
        if (nums.Length != 3) return null;
        var n = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(nums[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return null;
        var pre = dash >= 0 ? s[(dash + 1)..].Split('.') : [];
        if (pre.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        return new SemVersion(n[0], n[1], n[2], pre);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release ranks above any of its pre-releases.
        if (IsPreRelease != other.IsPreRelease) return IsPreRelease ? -1 : 1;
        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var (a, b) = (PreRelease[i], other.PreRelease[i]);
            var aNum = long.TryParse(a, out var an);
            var bNum = long.TryParse(b, out var bn);
            c = aNum && bNum ? an.CompareTo(bn)
              : aNum ? -1          // numeric identifiers rank below alphanumeric ones
              : bNum ? 1
              : string.CompareOrdinal(a, b);
            if (c != 0) return Math.Sign(c);
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }
}
