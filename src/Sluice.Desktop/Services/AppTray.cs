using Avalonia;
using Avalonia.Controls;
using Sluice.Desktop.ViewModels;

namespace Sluice.Desktop.Services;

/// <summary>Notification-area icon (Windows tray, Linux status notifier): open, toggle capture, exit.</summary>
public sealed class AppTray : IDisposable
{
    private readonly Application _app;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _capture;
    private readonly MainViewModel _vm;
    private bool? _shownActive;

    public static AppTray? Current { get; private set; }

    public AppTray(Application app, MainViewModel vm, Action show, Action exit)
    {
        Current = this;
        _app = app;
        _vm = vm;
        var open = new NativeMenuItem("Open Sluice");
        open.Click += (_, _) => show();
        _capture = new NativeMenuItem("Capture traffic") { ToggleType = MenuItemToggleType.CheckBox };
        _capture.Click += (_, _) => vm.Intercepting = !vm.Intercepting;
        var quit = new NativeMenuItem("Exit");
        quit.Click += (_, _) => exit();

        _icon = new TrayIcon
        {
            ToolTipText = "Sluice",
            Menu = new NativeMenu { Items = { open, _capture, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => show();
        TrayIcon.SetIcons(app, [_icon]);

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Intercepting) or nameof(MainViewModel.Status)) Update();
            if (e.PropertyName is nameof(MainViewModel.Update)) ShowUpdateItem();
        };
        Update();
    }

    private NativeMenuItem? _updateItem;
    private NativeMenuItemSeparator? _updateSeparator;

    /// <summary>"Sluice 1.1.0 is available…" at the top of the menu while an update is waiting (#198).</summary>
    private void ShowUpdateItem()
    {
        var items = _icon.Menu!.Items;
        if (_updateItem is not null) { items.Remove(_updateItem); items.Remove(_updateSeparator!); _updateItem = null; }
        if (_vm.Update is not { } u) return;
        _updateItem = new NativeMenuItem($"Sluice {u.Version} is available…");
        _updateItem.Click += (_, _) => _vm.DownloadUpdateCommand.Execute(null);
        _updateSeparator = new NativeMenuItemSeparator();
        items.Insert(0, _updateSeparator);
        items.Insert(0, _updateItem);
    }

    private void Update()
    {
        if (_shownActive != _vm.Intercepting)
        {
            _shownActive = _vm.Intercepting;
            _icon.Icon = AppIcon.WindowIcon(_vm.Intercepting);
        }
        _capture.IsChecked = _vm.Intercepting;
        var text = $"Sluice — {_vm.Status}  ↑{_vm.UpRate} ↓{_vm.DownRate}";
        _icon.ToolTipText = text.Length > 63 ? text[..63] : text;   // Windows tray limit
    }

    /// <summary>Shown once when the window is first hidden to the tray.</summary>
    private bool _hintShown;

    public void ShowHiddenHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        OsPlatform.Current.Notify("Sluice is still running", "Rules keep applying. Click the tray icon to reopen, or right-click it to exit.", false);
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
        TrayIcon.SetIcons(_app, []);
    }
}
