using Tmds.DBus.Protocol;

namespace Sluice.Desktop.Services;

/// <summary>
/// Whether a tray is there to show Sluice's icon (#45). Windows always has one. On Linux the icon is a
/// StatusNotifierItem, which only shows when a StatusNotifierWatcher with a host runs on the session bus: KDE and most
/// other desktops have one, stock GNOME has none without the AppIndicator extension. Without it, hiding the window
/// would leave no way back, so closing quits instead.
/// </summary>
internal static class TrayHost
{
    private const string Watcher = "org.kde.StatusNotifierWatcher";

    /// <summary>Asked when the window is closed, so a tray that appeared or went away since start-up counts. False
    /// when there is no session bus or it doesn't answer within <paramref name="timeout"/>: then the app quits rather
    /// than hiding where nobody can find it.</summary>
    public static async Task<bool> IsAvailableAsync(TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows()) return true;
        if (DBusAddress.Session is not { } address) return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync().AsTask().WaitAsync(cts.Token);
            if (!await NameHasOwnerAsync(connection).WaitAsync(cts.Token)) return false;
            try { return await HostRegisteredAsync(connection).WaitAsync(cts.Token); }
            catch (DBusErrorReplyException) { return true; }   // a watcher without the property: trust that it shows icons
        }
        catch (Exception ex) when (ex is DBusExceptionBase or OperationCanceledException or TimeoutException or IOException)
        {
            return false;
        }
    }

    private static Task<bool> NameHasOwnerAsync(DBusConnection connection)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus", member: "NameHasOwner", signature: "s");
            writer.WriteString(Watcher);
            message = writer.CreateMessage();
        }
        return connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadBool());
    }

    /// <summary>The watcher's <c>IsStatusNotifierHostRegistered</c>: something actually draws the icons.</summary>
    private static Task<bool> HostRegisteredAsync(DBusConnection connection)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: Watcher, path: "/StatusNotifierWatcher",
                @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
            writer.WriteString(Watcher);
            writer.WriteString("IsStatusNotifierHostRegistered");
            message = writer.CreateMessage();
        }
        return connection.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadSignature("b");   // the value is a boolean variant
            return reader.ReadBool();
        });
    }
}
