using System.Windows;
using Sluice.App.ViewModels;
using Forms = System.Windows.Forms;

namespace Sluice.App.Services;

/// <summary>Notification-area icon: open, toggle capture, exit.</summary>
public sealed class TrayIcon : IDisposable
{
    public static TrayIcon? Current { get; private set; }

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _capture;
    private readonly MainViewModel _vm;
    private readonly Action _show;
    private readonly Action _exit;
    private bool _hintShown;

    public TrayIcon(MainViewModel vm, Action show, Action exit)
    {
        _vm = vm;
        _show = show;
        _exit = exit;

        _capture = new Forms.ToolStripMenuItem("Capture traffic") { CheckOnClick = false };
        _capture.Click += (_, _) => vm.Intercepting = !vm.Intercepting;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Sluice", null, (_, _) => show());
        menu.Items.Add(_capture);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        menu.Opening += (_, _) => _capture.Checked = vm.Intercepting;

        _icon = new Forms.NotifyIcon
        {
            Text = "Sluice",
            Icon = AppIcon.TrayIcon(active: false),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Intercepting) or nameof(MainViewModel.Status)) Update();
        };
        Update();
        Current = this;
    }

    private bool? _shownActive;

    private void Update()
    {
        if (_shownActive != _vm.Intercepting)
        {
            _shownActive = _vm.Intercepting;
            var old = _icon.Icon;
            _icon.Icon = AppIcon.TrayIcon(_vm.Intercepting);
            old?.Dispose();
        }
        var text = $"Sluice — {_vm.Status}  ↑{_vm.UpRate} ↓{_vm.DownRate}";
        _icon.Text = text.Length > 63 ? text[..63] : text;   // NotifyIcon limit
    }

    /// <summary>A Windows notification from the tray icon.</summary>
    public void Notify(string title, string text, bool error) =>
        _icon.ShowBalloonTip(5000, title, text.Length > 250 ? text[..250] + "…" : text, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    public void ShowHiddenHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(3000, "Sluice is still running", "Rules keep applying. Double-click the icon to reopen, or right-click to exit.", Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (Current == this) Current = null;
    }
}
