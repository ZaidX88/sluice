using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Sluice.Core.Profiles;
using Sluice.Desktop.Views;
using Sluice.Desktop.Controls;

namespace Sluice.Desktop.Services;

/// <summary>
/// Modal UI used by view models. Avalonia dialogs are asynchronous; these helpers wait for them in a nested
/// dispatcher frame (the UI keeps running), so view models and Sluice.Core callbacks (master password) can stay
/// synchronous, as in the WPF app.
/// </summary>
public static class Dialogs
{
    /// <summary>
    /// The window a dialog belongs to: the active one, so a dialog opened from another dialog (the master password from
    /// Password protection, #150) stays on top of it; otherwise the main window when it is visible.
    /// </summary>
    private static Window? Owner
    {
        get
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return null;
            return desktop.Windows.FirstOrDefault(w => w is { IsActive: true, IsVisible: true })
                   ?? (desktop.MainWindow is { IsVisible: true } main ? main : null);
        }
    }

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

    /// <summary>How the current profile stores tunnel passwords (#150); changes apply as they're made.</summary>
    public static void EditPasswordProtection(EngineHost host) => ShowModal<bool>(new PasswordProtectionDialog(host));

    public static string? AskMasterPassword(string profileName, bool confirmNew)
    {
        var d = new PasswordDialog(profileName, confirmNew);
        return ShowModal<bool>(d) ? d.Password : null;
    }

    // ---------------- files ----------------

    private static IStorageProvider? Storage => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;

    private static readonly FilePickerFileType SluiceProfiles = new("Sluice profile") { Patterns = ["*.sluice"] };

    /// <summary>Picks a <c>.sluice</c> file anywhere, to copy into Sluice's profiles folder (#134).</summary>
    public static string? PickProfileToImport() => PickFile(OpenProfileAsync);

    /// <summary>
    /// Asks for a name (a new profile, Save as, Rename). <paramref name="check"/> returns why a name can't be used, or
    /// null; the dialog shows the reason and stays open until the name is fine or the user cancels.
    /// </summary>
    public static string? AskName(string title, string message, string initial, string okText, Func<string, string?> check)
    {
        var d = new NameDialog(title, message, initial, okText, check);
        return ShowModal<bool>(d) ? d.Result : null;
    }

    internal const string FileDialogFailedTitle = "File dialog";

    /// <summary>The message shown when a file picker fails (#24).</summary>
    internal static string FileDialogFailedText(string reason) =>
        $"The file dialog couldn't open: {reason}\n\nNothing was opened or saved. Sluice keeps running.";

    /// <summary>
    /// Runs a file picker. Any failure of the picker itself (the system's file chooser missing or refusing Sluice, a
    /// folder it can't show…) is shown as a message instead of closing the app (#24); the result is then null, as if the
    /// user had cancelled.
    /// </summary>
    private static string? PickFile(Func<Task<string?>> pick)
    {
        try { return Wait(pick()); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Info(FileDialogFailedTitle, FileDialogFailedText(ex.Message));
            return null;
        }
    }

    private static async Task<string?> OpenProfileAsync()
    {
        if (Storage is not { } s) return null;
        var files = await s.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a profile",
            FileTypeFilter =
            [
                SluiceProfiles,
                FilePickerFileTypes.All,
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}

/// <summary>Asks for a profile's name (#134), with the reason shown when a name can't be used.</summary>
public sealed class NameDialog : SluiceWindow
{
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, string?> _check;

    public string? Result { get; private set; }

    internal NameDialog(string title, string message, string initial, string okText, Func<string, string?> check)
    {
        _check = check;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _box = new TextBox { Text = initial };
        _error = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };
        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("Danger"));
        _box.TextChanged += (_, _) => _error.IsVisible = false;

        ControlTheme Theme(string key) => (ControlTheme)Application.Current!.FindResource(key)!;
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Theme = Theme("SecondaryButton"), IsCancel = true };
        var ok = new Button { Content = okText, MinWidth = 90, Theme = Theme("PrimaryButton"), IsDefault = true };
        cancel.Click += (_, _) => Close(false);
        ok.Click += (_, _) => Accept();

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Children =
            {
                new TextBlock { Text = title, Classes = { "PageTitle" }, FontSize = 17 },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) },
                _box,
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
                    Margin = new Thickness(0, 22, 0, 0), Children = { cancel, ok },
                },
            },
        };
        Opened += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Accept()
    {
        var name = (_box.Text ?? "").Trim();
        if (_check(name) is { } problem)
        {
            _error.Text = problem;
            _error.IsVisible = true;
            return;
        }
        Result = name;
        Close(true);
    }
}

/// <summary>Themed replacement for the WPF MessageBox.</summary>
public sealed class MessageDialog : SluiceWindow
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
        var d = Create(title, message, buttons, primary);
        return Dialogs.ShowModal<int?>(d) ?? buttons.Length - 1;
    }

    /// <summary>The dialog without showing it (also for <see cref="ScreenshotMode"/>).</summary>
    internal static MessageDialog Create(string title, string message, string[] buttons, int primary) =>
        new(title, message, buttons, primary) { Tag = buttons.Length - 1 };
}
