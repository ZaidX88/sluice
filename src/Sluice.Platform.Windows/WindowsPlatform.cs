using System.Diagnostics;
using System.Security;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Platform;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Sluice.Platform.Windows;

/// <summary>Windows implementations of the platform services.</summary>
public sealed class WindowsPlatform : IPlatformServices
{
    public string Name => "Windows";

    /// <summary>Notifications are titled after the Start Menu shortcut carrying our app ID (see <see cref="StartMenuShortcut"/>).</summary>
    public void OnAppStart() => StartMenuShortcut.Ensure();

    public void Notify(string title, string message, bool isError)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml($"""
                <toast{(isError ? " scenario=\"urgent\"" : "")}>
                  <visual><binding template="ToastGeneric"><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text></binding></visual>
                </toast>
                """);
            ToastNotificationManager.CreateToastNotifier(StartMenuShortcut.AppId).Show(new ToastNotification(xml));
        }
        catch (Exception)
        {
            // Notifications are a convenience; e.g. Focus Assist or missing shortcut must not break Sluice.
        }
    }

    /// <summary>WinDivert ships next to Sluice and works on every supported Windows.</summary>
    public string? CaptureUnavailableReason => null;

    public bool HasCaptureRights => Environment.IsPrivilegedProcess;

    public string CaptureRightsHint => "Capturing other applications' traffic needs administrator rights.";

    public bool CanRestartElevated => true;

    public ITrafficCapture CreateCapture(RoutingEngine engine) => new Interceptor(engine);

    /// <summary>
    /// Puts <paramref name="process"/> (a helper Sluice just started) in Sluice's kill-on-close job object
    /// (<see cref="HelperJob"/>): Windows ends it when Sluice ends, also when Sluice crashes or is killed. Never throws.
    /// </summary>
    public void TieChildProcess(Process process) => HelperJob.Shared?.Assign(process);

    /// <summary>Drops the Windows DNS cache (<c>DnsFlushResolverCache</c>, as <c>ipconfig /flushdns</c>). Never throws.</summary>
    public void FlushDnsCache() => DnsCache.Flush();
}
