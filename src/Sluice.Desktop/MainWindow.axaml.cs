using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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

        // #135: a press on something that can't take focus (empty space, text, a card) takes the caret out of the
        // search box; Avalonia leaves the focus where it was. Tunnel and handled events too, so it sees every press,
        // and the press itself goes on as before.
        AddHandler(PointerPressedEvent, ClearFocusOnEmptyPress, RoutingStrategies.Tunnel, handledEventsToo: true);
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);

        // An empty MenuFlyout doesn't open, so the menu is filled before the press that opens it (and whenever the
        // profile changes), not in its Opening event.
        BuildProfileMenu();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ProfileName)) BuildProfileMenu(); };
        ProfileButton.AddHandler(PointerPressedEvent, (_, _) => BuildProfileMenu(), RoutingStrategies.Tunnel, handledEventsToo: true);
        ProfileButton.AddHandler(KeyDownEvent, (_, _) => BuildProfileMenu(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// The profile menu (#134): every profile in Sluice's folder, the open one ticked, a click switching at once; then
    /// new, save as, rename, delete, password protection (#150), import and the folder. Rebuilt on each press of the profile button, so it shows the
    /// folder as it is.
    /// </summary>
    internal MenuFlyout ProfileMenu => (MenuFlyout)ProfileButton.Flyout!;

    internal void BuildProfileMenu()
    {
        if (DataContext is not MainViewModel vm) return;
        var items = ProfileMenu.Items;
        items.Clear();
        foreach (var name in vm.ProfileNames)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = name },   // a TextBlock, so an "_" in a name isn't an access key
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "profiles",
                IsChecked = vm.CanManageProfile && string.Equals(name, vm.ProfileName, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => vm.SwitchProfile(name);
            items.Add(item);
        }
        if (items.Count > 0) items.Add(new Separator());
        items.Add(new MenuItem { Header = "New profile…", Command = vm.NewProfileCommand });
        items.Add(new MenuItem { Header = "Save as…", Command = vm.SaveAsCommand });
        items.Add(new MenuItem { Header = "Rename…", Command = vm.RenameProfileCommand, IsEnabled = vm.CanManageProfile });
        items.Add(new MenuItem { Header = "Delete…", Command = vm.DeleteProfileCommand, IsEnabled = vm.CanManageProfile });
        items.Add(new MenuItem { Header = "Password protection…", Command = vm.PasswordProtectionCommand, IsEnabled = vm.CanManageProfile });
        items.Add(new Separator());
        items.Add(new MenuItem { Header = "Import profile file…", Command = vm.ImportProfileCommand });
        items.Add(new MenuItem { Header = "Show profiles folder", Command = vm.ShowProfilesFolderCommand });
    }

    /// <summary>
    /// Clears the focus unless the press landed in something focusable: the press usually hits an inner part (the
    /// text in a button, the border of a list row), so the element and its parents up to the window are checked.
    /// </summary>
    private void ClearFocusOnEmptyPress(object? sender, PointerPressedEventArgs e)
    {
        for (var v = e.Source as Visual; v is not null && v != this; v = v.GetVisualParent())
            if (v is InputElement { Focusable: true, IsEffectivelyEnabled: true }) return;
        FocusManager?.Focus(null);
    }

    /// <summary>Esc in the search box clears the search, if there is one, and takes the focus out of the box.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
        FocusManager?.Focus(null);
        e.Handled = true;
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
