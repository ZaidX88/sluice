using Avalonia.Controls;
using Sluice.Desktop.Services;
using Sluice.Desktop.ViewModels;
using Sluice.Desktop.Controls;

namespace Sluice.Desktop;

public partial class MainWindow : SluiceWindow
{
    private readonly AppSettings? _settings;

    /// <summary>True when the app is really exiting (tray "Exit"), not just hiding the window.</summary>
    public bool AllowClose { get; set; }

    public MainWindow() => InitializeComponent();   // designer

    public MainWindow(MainViewModel vm, AppSettings settings) : this()
    {
        DataContext = vm;
        _settings = settings;
        Logo.Source = AppIcon.Image(64, inverse: App.IsDark);
        Icon = AppIcon.WindowIcon();
        ActualThemeVariantChanged += (_, _) => Logo.Source = AppIcon.Image(64, inverse: App.IsDark);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && _settings is { MinimizeToTray: true })
        {
            e.Cancel = true;
            if (!_deciding) _ = HideOrQuitAsync();
            return;
        }
        base.OnClosing(e);
    }

    private bool _deciding;

    /// <summary>
    /// Hides to the tray only when there is one to come back from (#45): stock GNOME has none, and a hidden window
    /// could then only be reached by killing Sluice. Without a tray, closing quits.
    /// </summary>
    private async Task HideOrQuitAsync()
    {
        _deciding = true;
        try
        {
            if (await TrayHost.IsAvailableAsync(TimeSpan.FromSeconds(2)))
            {
                Hide();
                AppTray.Current?.ShowHiddenHint();
            }
            else
            {
                App.Exit();
            }
        }
        finally { _deciding = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!AllowClose) App.OnMainWindowClosed();
    }
}
