using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Sluice.Desktop.Services;

/// <summary>
/// The Sluice mark, drawn in code: a rounded black tile with three white flowing lines,
/// one of them diverted (a sluice gate redirecting flow). Monochrome to match the UI.
/// </summary>
public static class AppIcon
{
    private static readonly Color Ink = Color.FromRgb(0x0A, 0x0A, 0x0A);

    /// <param name="active">False draws the idle (grey) variant used by the tray while capture is off.</param>
    /// <param name="inverse">White tile with black lines (for the in-app logo on dark backgrounds).</param>
    public static Bitmap Image(int size, bool active = true, bool inverse = false)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var dc = bmp.CreateDrawingContext())
        {
            double s = size;
            var tile = inverse ? Color.FromRgb(0xFA, 0xFA, 0xFA) : active ? Ink : Color.FromRgb(0x8E, 0x8E, 0x96);
            var line = inverse ? Ink : Colors.White;
            dc.DrawRectangle(new SolidColorBrush(tile), null, new Rect(0, 0, s, s), s * 0.24, s * 0.24);

            var pen = new Pen(new SolidColorBrush(line), Math.Max(1.5, s * 0.085), lineCap: PenLineCap.Round);
            double l = s * 0.24, r = s * 0.76;
            // Top and bottom streams flow straight through.
            dc.DrawLine(pen, new Point(l, s * 0.32), new Point(r, s * 0.32));
            dc.DrawLine(pen, new Point(l, s * 0.68), new Point(r, s * 0.68));
            // Middle stream is diverted upward through the gate.
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(l, s * 0.5), false);
                c.LineTo(new Point(s * 0.46, s * 0.5));
                c.CubicBezierTo(new Point(s * 0.58, s * 0.5), new Point(s * 0.6, s * 0.44), new Point(r, s * 0.44));
                c.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xCC, line.R, line.G, line.B)), pen.Thickness, lineCap: PenLineCap.Round), g);
        }
        return bmp;
    }

    public static WindowIcon WindowIcon(bool active = true) => new(Image(64, active));
}
