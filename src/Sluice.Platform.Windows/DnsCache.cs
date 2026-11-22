using System.Runtime.InteropServices;

namespace Sluice.Platform.Windows;

/// <summary>The Windows DNS client's cache (what <c>ipconfig /flushdns</c> clears).</summary>
internal static class DnsCache
{
    /// <summary>Drops the cached answers. False when Windows refused (the DNS Client service is stopped) or dnsapi is
    /// missing. Never throws.</summary>
    public static bool Flush()
    {
        try
        {
            return DnsFlushResolverCache();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();
}
