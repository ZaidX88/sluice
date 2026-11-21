using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Sluice.Desktop.Controls;

/// <summary>
/// Every Sluice window (the main window and the dialogs) derives from this (#44). Where the window system lets the app
/// draw into the title bar, it turns the system title bar off and uses its own window template: a 1 px
/// <c>WindowBorder</c> (black in light, transparent in dark) around Sluice's <see cref="TitleBar"/> and the window's
/// content. The content keeps its space: the bar's height is added to the window's height, minimum and maximum height.
/// A new window gets all of this by deriving from <see cref="SluiceWindow"/>; nothing else to do.
/// </summary>
public class SluiceWindow : Window
{
    private Border? _frame;
    private Panel? _resizeGrips;
    private bool _sized;
    private bool _framesItself;   // X11: no window manager frame, so moving and resizing are Sluice's own

    /// <summary>True when this window draws its own title bar (decided once, in <see cref="SetUpForPlatform"/>).</summary>
    public bool DrawsOwnTitleBar { get; }

    public SluiceWindow()
    {
        DrawsOwnTitleBar = SetUpForPlatform();
        if (!DrawsOwnTitleBar) return;
        Template = new FuncControlTemplate<SluiceWindow>(BuildTemplate);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == OffScreenMarginProperty || e.Property == CanResizeProperty) UpdateFrame();
        };
    }

    /// <summary>
    /// The one place for OS-specific window setup, decided by what this window's platform handle is, not by the OS:
    /// <list type="bullet">
    /// <item>a Win32 window (HWND) extends its client area over the title bar, keeping Windows' resize edges, shadow,
    /// rounded corners and snapping;</item>
    /// <item>an X11 window (XID; on Wayland desktops through XWayland) has no window manager decorations at all: the
    /// title bar moves the window (<see cref="Window.BeginMoveDrag"/>) and maximises it on a double-click, and thin
    /// strips at the edges and corners resize it (<see cref="Window.BeginResizeDrag"/>), all through the window
    /// manager, so its keyboard shortcuts (Alt+F4, Super+arrows) keep working (#84).</item>
    /// </list>
    /// Any other window system keeps its own title bar.
    /// </summary>
    private bool SetUpForPlatform()
    {
        switch (TryGetPlatformHandle()?.HandleDescriptor)
        {
            case "HWND":
                ExtendClientAreaToDecorationsHint = true;
                WindowDecorations = WindowDecorations.BorderOnly;   // Windows' frame without its title bar; Avalonia draws none
                return true;
            case "XID":
                WindowDecorations = WindowDecorations.None;          // no window manager title bar or frame
                _framesItself = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Border → layer manager (popups, adorners) → title bar above the content presenter.</summary>
    private Control BuildTemplate(SluiceWindow window, INameScope scope)
    {
        var presenter = new ContentPresenter { Name = "PART_ContentPresenter" };
        presenter.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentProperty));
        presenter.Bind(ContentPresenter.ContentTemplateProperty, new TemplateBinding(ContentTemplateProperty));
        presenter.Bind(ContentPresenter.HorizontalContentAlignmentProperty, new TemplateBinding(HorizontalContentAlignmentProperty));
        presenter.Bind(ContentPresenter.VerticalContentAlignmentProperty, new TemplateBinding(VerticalContentAlignmentProperty));
        presenter.Bind(MarginProperty, new TemplateBinding(PaddingProperty));
        scope.Register(presenter.Name, presenter);

        var bar = new TitleBar(window, movesWindow: _framesItself);
        DockPanel.SetDock(bar, Dock.Top);

        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            Child = new VisualLayerManager { Child = new DockPanel { Children = { bar, presenter } } },
        };
        _frame.Bind(Border.BackgroundProperty, new TemplateBinding(BackgroundProperty));
        _frame.Bind(Border.BorderBrushProperty, _frame.GetResourceObservable("WindowBorder"));
        if (!_framesItself)
        {
            UpdateFrame();
            return _frame;
        }
        _resizeGrips = ResizeGrips();
        UpdateFrame();
        return new Panel { Children = { _frame, _resizeGrips } };
    }

    /// <summary>
    /// Invisible strips along the edges and squares at the corners, above the content: pressing one hands the resize to
    /// the window manager. Shown only while the window can be resized and isn't maximised.
    /// </summary>
    private Panel ResizeGrips()
    {
        const double edge = 5, corner = 12;
        var grips = new Panel();
        void Grip(WindowEdge side, HorizontalAlignment h, VerticalAlignment v, double width, double height, StandardCursorType cursor)
        {
            var grip = new Border { Background = Brushes.Transparent, HorizontalAlignment = h, VerticalAlignment = v, Cursor = new Cursor(cursor) };
            if (!double.IsNaN(width)) grip.Width = width;
            if (!double.IsNaN(height)) grip.Height = height;
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
                BeginResizeDrag(side, e);
                e.Handled = true;
            };
            grips.Children.Add(grip);
        }
        Grip(WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, edge, StandardCursorType.TopSide);
        Grip(WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, edge, StandardCursorType.BottomSide);
        Grip(WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, edge, double.NaN, StandardCursorType.LeftSide);
        Grip(WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, edge, double.NaN, StandardCursorType.RightSide);
        Grip(WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, corner, corner, StandardCursorType.TopLeftCorner);
        Grip(WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, corner, corner, StandardCursorType.TopRightCorner);
        Grip(WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, corner, corner, StandardCursorType.BottomLeftCorner);
        Grip(WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, corner, corner, StandardCursorType.BottomRightCorner);
        return grips;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        AddTitleBarHeight();
    }

    protected override void OnOpened(EventArgs e)
    {
        AddTitleBarHeight();   // windows built in code may not be "initialized" before they open
        base.OnOpened(e);
    }

    /// <summary>The bar sits inside the client area, so the window grows by its height and the content keeps its space.</summary>
    private void AddTitleBarHeight()
    {
        if (!DrawsOwnTitleBar || _sized) return;
        _sized = true;
        if (SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight) && !double.IsNaN(Height)) Height += TitleBar.BarHeight;
        if (MinHeight > 0) MinHeight += TitleBar.BarHeight;
        if (!double.IsInfinity(MaxHeight)) MaxHeight += TitleBar.BarHeight;
    }

    private void UpdateFrame()
    {
        if (_frame is null) return;
        // Maximised, Windows puts the frame just off the screen: keep the content on it, and drop the edge line.
        var maximised = WindowState == WindowState.Maximized;
        _frame.Margin = maximised ? OffScreenMargin : default;
        _frame.BorderThickness = new Thickness(maximised ? 0 : 1);
        if (_resizeGrips is not null) _resizeGrips.IsVisible = CanResize && !maximised;
    }
}
