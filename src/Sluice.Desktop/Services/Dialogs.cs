using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sluice.Core.Profiles;
using Sluice.Desktop.Views;

namespace Sluice.Desktop.Services;

/// <summary>
/// Modal UI used by view models. Avalonia dialogs are asynchronous; these helpers wait for them in a nested
/// dispatcher frame (the UI keeps running), so view models and Sluice.Core callbacks (master password) can stay
/// synchronous, as in the WPF app.
/// </summary>
public static class Dialogs
{
    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is { IsVisible: true } w ? w : null;

    /// <summary>Runs an asynchronous UI operation to completion without blocking the UI thread.</summary>
    internal static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Shows <paramref name="dialog"/> modally (over the main window when it is visible).</summary>
    internal static T? ShowModal<T>(Window dialog)
    {
        if (Owner is { } owner) return Wait(dialog.ShowDialog<T?>(owner));
        var done = new TaskCompletionSource<T?>();
        dialog.Closed += (_, _) => done.TrySetResult(dialog.Tag is T t ? t : default);
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        return Wait(done.Task);
    }

    public static bool Confirm(string title, string message, string confirmText) =>
        MessageDialog.Show(title, message, [confirmText, "Cancel"], primary: 0) == 0;

    /// <summary>Yes / No / Cancel → true / false / null.</summary>
    public static bool? Ask(string title, string message) =>
        MessageDialog.Show(title, message, ["Yes", "No", "Cancel"], primary: 0) switch { 0 => true, 1 => false, _ => null };

    public static void Info(string title, string message) => MessageDialog.Show(title, message, ["OK"], primary: 0);

    public static Tunnel? EditTunnel(Tunnel? existing)
    {
        var d = new TunnelDialog(existing);
        return ShowModal<bool>(d) ? d.Result : null;
    }

    public static Sluice.Core.Tunnels.ImportResult? Import(Profile profile, Sluice.Core.Tunnels.TunnelTransport.Resolver? resolve)
    {
        var d = new ImportDialog(profile, resolve, Wait(ImportDialog.ClipboardLinksAsync()));
        return ShowModal<bool>(d) ? d.Result : null;
    }

    public static Rule? EditRule(Rule? existing, Profile profile)
    {
        var d = new RuleDialog(existing, profile);
        return ShowModal<bool>(d) ? d.Result : null;
    }

    public static string? AskMasterPassword(string profileName, bool confirmNew)
    {
        var d = new PasswordDialog(profileName, confirmNew);
        return ShowModal<bool>(d) ? d.Password : null;
    }

    // ---------------- files ----------------

    private static IStorageProvider? Storage => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;

    private static readonly FilePickerFileType SluiceProfiles = new("Sluice profile") { Patterns = ["*.sluice"] };
    private static readonly FilePickerFileType ProxifierProfiles = new("Proxifier profile") { Patterns = ["*.ppx", "*.prx"] };

    public static string? OpenProfile() => Wait(OpenProfileAsync());

    private static async Task<string?> OpenProfileAsync()
    {
        if (Storage is not { } s) return null;
        var files = await s.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open profile",
            FileTypeFilter =
            [
                new FilePickerFileType("Profiles") { Patterns = ["*.sluice", "*.ppx", "*.prx"] },
                SluiceProfiles,
                new FilePickerFileType("Proxifier profiles (imported)") { Patterns = ["*.ppx", "*.prx"] },
                FilePickerFileTypes.All,
            ],
            SuggestedStartLocation = await DefaultFolderAsync(s),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static string? SaveProfile(string name) => Wait(SaveAsync("Save profile as", name, ".sluice", SluiceProfiles));

    public static string? ExportProxifier(string name) => Wait(SaveAsync("Export for Proxifier", name, ".ppx", ProxifierProfiles));

    private static async Task<string?> SaveAsync(string title, string name, string extension, FilePickerFileType type)
    {
        if (Storage is not { } s) return null;
        var file = await s.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = name + extension,
            DefaultExtension = extension,
            FileTypeChoices = [type],
            SuggestedStartLocation = await DefaultFolderAsync(s),
        });
        return file?.TryGetLocalPath();
    }

    private static async Task<IStorageFolder?> DefaultFolderAsync(IStorageProvider s)
    {
        Directory.CreateDirectory(AppSettings.ProfilesFolder);
        return await s.TryGetFolderFromPathAsync(AppSettings.ProfilesFolder);
    }
}

/// <summary>Themed replacement for the WPF MessageBox.</summary>
public sealed class MessageDialog : Window
{
    private MessageDialog(string title, string message, string[] buttons, int primary)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 22, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = buttons[i],
                MinWidth = 90,
                Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource(i == primary ? "PrimaryButton" : "SecondaryButton")!,
                IsDefault = i == primary,
                IsCancel = i == buttons.Length - 1 && buttons.Length > 1,
            };
            b.Click += (_, _) => { Tag = index; Close(index); };
            row.Children.Add(b);
        }
        Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Children =
            {
                new TextBlock { Text = title, Classes = { "PageTitle" }, FontSize = 17 },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) },
                row,
            },
        };
    }

    /// <returns>Index of the button pressed, or the last one (Cancel) when the window is closed.</returns>
    public static int Show(string title, string message, string[] buttons, int primary)
    {
        var d = new MessageDialog(title, message, buttons, primary) { Tag = buttons.Length - 1 };
        return Dialogs.ShowModal<int?>(d) ?? buttons.Length - 1;
    }
}
