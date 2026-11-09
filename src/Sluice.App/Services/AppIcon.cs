using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Sluice.App.Services;

/// <summary>
/// The Sluice mark, drawn in code: a rounded black tile with three white flowing lines,
/// one of them diverted (a sluice gate redirecting flow). Monochrome to match the UI.
/// </summary>
public static class AppIcon
{
    private static readonly Color Ink = Color.FromRgb(0x0A, 0x0A, 0x0A);

    /// <param name="active">False draws the idle (grey) variant used by the tray while capture is off.</param>
    /// <param name="inverse">White tile with black lines (for the in-app logo on dark backgrounds).</param>
    public static BitmapSource Image(int size, bool active = true, bool inverse = false)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var s = size;
            var tile = inverse ? Color.FromRgb(0xFA, 0xFA, 0xFA) : active ? Ink : Color.FromRgb(0x8E, 0x8E, 0x96);
            var line = inverse ? Ink : Colors.White;
            var bg = new SolidColorBrush(tile);
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, s, s), s * 0.24, s * 0.24);

            var pen = new Pen(new SolidColorBrush(line), Math.Max(1.5, s * 0.085)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double l = s * 0.24, r = s * 0.76;
            // Top and bottom streams flow straight through.
            dc.DrawLine(pen, new Point(l, s * 0.32), new Point(r, s * 0.32));
            dc.DrawLine(pen, new Point(l, s * 0.68), new Point(r, s * 0.68));
            // Middle stream is diverted upward through the gate.
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(l, s * 0.5), false, false);
                c.LineTo(new Point(s * 0.46, s * 0.5), true, true);
                c.BezierTo(new Point(s * 0.58, s * 0.5), new Point(s * 0.6, s * 0.44), new Point(r, s * 0.44), true, true);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xCC, line.R, line.G, line.B)), pen.Thickness)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>GDI icon for the tray (NotifyIcon needs System.Drawing.Icon).</summary>
    public static Drawing.Icon TrayIcon(bool active)
    {
        var src = Image(32, active);
        var stride = 32 * 4;
        var pixels = new byte[stride * 32];
        src.CopyPixels(pixels, stride, 0);
        using var bmp = new Drawing.Bitmap(32, 32, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, 32, 32), Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bmp.UnlockBits(data);
        var h = bmp.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);
}
