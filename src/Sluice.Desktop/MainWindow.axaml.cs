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
            Hide();
            AppTray.Current?.ShowHiddenHint();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!AllowClose) App.OnMainWindowClosed();
    }
}
