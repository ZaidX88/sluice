using System.ComponentModel;
using System.Windows;
using Sluice.App.Services;
using Sluice.App.ViewModels;

namespace Sluice.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>True when the app is really exiting (tray "Exit"), not just hiding the window.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        DataContext = vm;
        _settings = settings;
        Logo.Source = AppIcon.Image(64, inverse: App.IsDark);
        Icon = AppIcon.Image(32);
    }

    private void OnProfileMenu(object sender, RoutedEventArgs e)
    {
        var menu = ProfileButton.ContextMenu!;
        menu.PlacementTarget = ProfileButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            TrayIcon.Current?.ShowHiddenHint();
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
