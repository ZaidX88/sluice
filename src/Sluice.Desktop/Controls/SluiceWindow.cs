using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;

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
    private bool _sized;

    /// <summary>True when this window draws its own title bar (decided once, in <see cref="SetUpForPlatform"/>).</summary>
    public bool DrawsOwnTitleBar { get; }

    public SluiceWindow()
    {
        DrawsOwnTitleBar = SetUpForPlatform();
        if (!DrawsOwnTitleBar) return;
        Template = new FuncControlTemplate<SluiceWindow>(BuildTemplate);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == OffScreenMarginProperty) UpdateFrame();
        };
    }

    /// <summary>
    /// The one place for OS-specific window setup, decided by what this window's platform handle is, not by the OS:
    /// a Win32 window (HWND) extends its client area over the title bar, keeping Windows' resize edges, shadow,
    /// rounded corners and snapping. Other window systems keep their own title bar for now (Linux: #84).
    /// </summary>
    private bool SetUpForPlatform()
    {
        if (TryGetPlatformHandle()?.HandleDescriptor != "HWND") return false;
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.BorderOnly;   // Windows' frame without its title bar; Avalonia draws none
        return true;
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

        var bar = new TitleBar(window);
        DockPanel.SetDock(bar, Dock.Top);

        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            Child = new VisualLayerManager { Child = new DockPanel { Children = { bar, presenter } } },
        };
        _frame.Bind(Border.BackgroundProperty, new TemplateBinding(BackgroundProperty));
        _frame.Bind(Border.BorderBrushProperty, _frame.GetResourceObservable("WindowBorder"));
        UpdateFrame();
        return _frame;
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
    }
}
