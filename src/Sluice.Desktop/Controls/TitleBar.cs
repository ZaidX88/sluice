using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Sluice.Desktop.Services;

namespace Sluice.Desktop.Controls;

/// <summary>
/// Sluice's own title bar (#44): icon, window title, and the minimise, maximise / restore and close buttons (a window
/// that can't be resized shows close only). Colours come from the theme (<c>TitleBarBg</c>, <c>TitleBarText</c>), so
/// it follows a theme change at once. Each part tells the window system what it is
/// (<see cref="WindowDecorationProperties.ElementRoleProperty"/>): the bar moves the window and a double-click maximises
/// it, and the maximise button gets Windows 11's snap layouts. Where the window system doesn't use those roles (X11,
/// <paramref name="movesWindow"/>), the bar does both itself through the window manager.
/// </summary>
public sealed class TitleBar : Border
{
    public const double BarHeight = 32;

    private readonly Window _window;
    private readonly Image _icon = new() { Width = 16, Height = 16, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _maxGlyph;
    private readonly Button _min, _max;

    private const string MinimiseData = "M0,5.5 H10";
    private const string MaximiseData = "M0.5,0.5 H9.5 V9.5 H0.5 Z";
    private const string RestoreData = "M0.5,2.5 H7.5 V9.5 H0.5 Z M2.5,2.5 V0.5 H9.5 V7.5 H7.5";
    private const string CloseData = "M0,0 L10,10 M10,0 L0,10";

    /// <param name="window">The window this bar belongs to.</param>
    /// <param name="movesWindow">True when the bar moves the window and maximises it on a double-click itself.</param>
    public TitleBar(Window window, bool movesWindow = false)
    {
        _window = window;
        Height = BarHeight;
        this.Bind(BackgroundProperty, this.GetResourceObservable("TitleBarBg"));
        WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.TitleBar);

        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, window.GetObservable(Window.TitleProperty));
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("TitleBarText"));

        _min = CaptionButton("Minimise", Glyph(MinimiseData), WindowDecorationsElementRole.MinimizeButton,
            () => _window.WindowState = WindowState.Minimized);
        _maxGlyph = Glyph(MaximiseData);
        _max = CaptionButton("Maximise", _maxGlyph, WindowDecorationsElementRole.MaximizeButton, ToggleMaximised);
        var close = CaptionButton("Close", Glyph(CloseData), WindowDecorationsElementRole.CloseButton, _window.Close);
        close.Classes.Add("Close");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _min, _max, close } };
        DockPanel.SetDock(_icon, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        Child = new DockPanel { Children = { _icon, buttons, title } };

        UpdateButtons();
        UpdateIcon();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty) UpdateButtons();
        };
        window.ActualThemeVariantChanged += (_, _) => UpdateIcon();
        if (movesWindow) PointerPressed += OnPressed;   // the caption buttons handle their own presses
    }

    /// <summary>Drag to move (the window manager moves it); a double-click maximises or restores a resizable window.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { if (_window.CanResize) ToggleMaximised(); }
        else _window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void ToggleMaximised() =>
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateButtons()
    {
        _min.IsVisible = _max.IsVisible = _window.CanResize;
        var maximised = _window.WindowState == WindowState.Maximized;
        _maxGlyph.Data = Geometry.Parse(maximised ? RestoreData : MaximiseData);
        ToolTip.SetTip(_max, maximised ? "Restore" : "Maximise");
    }

    private void UpdateIcon() => _icon.Source = AppIcon.Image(32, inverse: App.IsDark);

    private static Path Glyph(string data) =>
        new() { Data = Geometry.Parse(data), StrokeThickness = 1, Width = 10, Height = 10, Stretch = Stretch.None, Classes = { "CaptionGlyph" } };

    private static Button CaptionButton(string tip, Path glyph, WindowDecorationsElementRole role, Action click)
    {
        var b = new Button { Content = glyph, Focusable = false };
        if (Application.Current?.FindResource("CaptionButton") is ControlTheme theme) b.Theme = theme;
        glyph.Bind(Shape.StrokeProperty, b.GetObservable(TemplatedControl.ForegroundProperty));   // white on the red close hover
        ToolTip.SetTip(b, tip);
        WindowDecorationProperties.SetElementRole(b, role);
        b.Click += (_, _) => click();
        return b;
    }
}
