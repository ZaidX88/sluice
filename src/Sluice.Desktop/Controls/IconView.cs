using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Sluice.Desktop.Controls;

/// <summary>
/// A small line icon drawn from vector paths (24 x 24 design grid) in the current text colour. Replaces the
/// Segoe Fluent Icons font the WPF app used, which does not exist on Linux.
/// </summary>
public sealed class IconView : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Kind));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<IconView, double>(nameof(Size), 15);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();

    static IconView() => AffectsRender<IconView>(KindProperty, SizeProperty, ForegroundProperty);

    /// <summary>Icon name, e.g. "add", "globe", "play" (see <see cref="Paths"/>).</summary>
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Kind is null || !Paths.TryGetValue(Kind, out var icon) || Foreground is null) return;
        var scale = Size / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) *
                                     Matrix.CreateTranslation((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2)))
        {
            var geometry = Cache.TryGetValue(Kind, out var g) ? g : Cache[Kind] = Geometry.Parse(icon.Data);
            if (icon.Filled) context.DrawGeometry(Foreground, null, geometry);
            else context.DrawGeometry(null, new Pen(Foreground, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }

    private static readonly Dictionary<string, Geometry> Cache = new();

    private sealed record IconPath(string Data, bool Filled = false);

    /// <summary>The icon set (24 x 24, drawn as 1.8-unit round strokes unless filled).</summary>
    private static readonly Dictionary<string, IconPath> Paths = new()
    {
        ["globe"] = new("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18Z M3 12h18 M12 3c2.4 2.5 3.6 5.5 3.6 9s-1.2 6.5-3.6 9c-2.4-2.5-3.6-5.5-3.6-9s1.2-6.5 3.6-9Z"),
        ["chart"] = new("M3 20h18 M5 16l4.5-5 3.5 3.5 6-7.5"),
        ["list"] = new("M9 6h11 M9 12h11 M9 18h11 M4.5 6h0.1 M4.5 12h0.1 M4.5 18h0.1"),
        ["tunnel"] = new("M4 20v-8a8 8 0 0 1 16 0v8 M8.5 20v-7a3.5 3.5 0 0 1 7 0v7 M3 20h18"),
        ["filter"] = new("M4 5h16l-6.2 7.6V19l-3.6-1.8v-4.6Z"),
        ["settings"] = new("M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6Z M12 2.5v3 M12 18.5v3 M2.5 12h3 M18.5 12h3 M5.3 5.3l2.1 2.1 M16.6 16.6l2.1 2.1 M5.3 18.7l2.1-2.1 M16.6 7.4l2.1-2.1"),
        ["add"] = new("M12 5v14 M5 12h14"),
        ["check"] = new("M5 12.5l4.5 4.5L19 7.5"),
        ["dash"] = new("M7 12h10"),
        ["download"] = new("M12 4v11 M7 10.5l5 5 5-5 M5 20h14"),
        ["pulse"] = new("M3 12h4l2.5-6 4.5 12 2.5-6H21"),
        ["link"] = new("M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1 M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"),
        ["refresh"] = new("M20 12a8 8 0 1 1-2.4-5.7 M20 4v4.5h-4.5"),
        ["close"] = new("M6.5 6.5l11 11 M17.5 6.5l-11 11"),
        ["info"] = new("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18Z M12 11v5.5 M12 7.6h0.1"),
        ["warning"] = new("M12 3.5L2.5 20h19Z M12 10v4.5 M12 17.4h0.1"),
        ["chevron-down"] = new("M6 9.5l6 6 6-6"),
        ["chevron-up"] = new("M6 14.5l6-6 6 6"),
        ["search"] = new("M10.5 4a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13Z M15.5 15.5L20 20"),
        ["arrow-up"] = new("M12 19V5 M6 11l6-6 6 6"),
        ["arrow-down"] = new("M12 5v14 M6 13l6 6 6-6"),
        ["play"] = new("M8 5.2v13.6a0.8 0.8 0 0 0 1.2 0.7l10.6-6.8a0.8 0.8 0 0 0 0-1.4L9.2 4.5A0.8 0.8 0 0 0 8 5.2Z", Filled: true),
        ["pause"] = new("M7 5h3.2v14H7Z M13.8 5H17v14h-3.2Z", Filled: true),
    };
}
