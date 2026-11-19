using System.Reflection;
using System.Runtime.InteropServices;

namespace Sluice.Core;

/// <summary>
/// The version of this Sluice build, set once in <c>Directory.Build.props</c> (<c>&lt;Version&gt;</c>, issue #91) and
/// shown the same way by the CLI (<c>--version</c>), the log files' first line and the desktop app.
/// </summary>
public static class SluiceVersion
{
    /// <summary>"1.0.0-rc.1+abc1234": the version with the commit it was built from, shortened to 7 characters.</summary>
    public static string Text { get; } = Shorten(
        typeof(SluiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SluiceVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0");

    /// <summary>"Windows 10.0.22000" or the Linux kernel's description.</summary>
    public static string OperatingSystemName => OperatingSystem.IsWindows()
        ? $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Environment.OSVersion.Version.Build}"
        : RuntimeInformation.OSDescription;

    /// <summary>"Sluice 1.0.0-rc.1+abc1234 on Windows 10.0.22000": the first line of every log.</summary>
    public static string Banner => $"Sluice {Text} on {OperatingSystemName}";

    /// <summary>Shortens a commit hash after "+" to 7 characters; anything else is left as it is.</summary>
    internal static string Shorten(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0) return informational;
        var meta = informational[(plus + 1)..];
        return meta.Length > 7 && meta.All(Uri.IsHexDigit) ? informational[..(plus + 1)] + meta[..7] : informational;
    }
}
