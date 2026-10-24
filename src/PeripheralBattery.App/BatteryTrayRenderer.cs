using System.Drawing.Drawing2D;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal readonly record struct BatteryRing(int? Percent, bool Low)
{
    internal static BatteryRing From(Reading reading, int lowThreshold) =>
        reading.State == BatteryState.Valid && reading.Percent is >= 0 and <= 100
            ? new(reading.Percent, reading.Percent <= lowThreshold)
            : new(null, reading.State == BatteryState.LowBatteryWarning);
}

internal readonly record struct BatteryRings(BatteryRing Outer, BatteryRing Inner, TrayColorTheme Theme = TrayColorTheme.Monochrome)
{
    internal static BatteryRings From(Reading mouse, Reading keyboard, AppSettings settings)
    {
        var m = BatteryRing.From(mouse, settings.LowThreshold);
        var k = BatteryRing.From(keyboard, settings.LowThreshold);
        return settings.KeyboardOuterRing ? new(k, m, settings.ColorTheme) : new(m, k, settings.ColorTheme);
    }
}

internal static class BatteryTrayRenderer
{
    internal static readonly Color Normal = Color.FromArgb(24, 24, 27);
    internal static readonly Color Low = Color.FromArgb(243, 75, 80);
    internal static readonly Color Consumed = Color.FromArgb(250, 250, 252);
    internal static readonly Color Unknown = Color.FromArgb(145, 151, 161);
    internal static Color NormalColor(TrayColorTheme theme) => theme switch
    {
        TrayColorTheme.Teal => Color.FromArgb(54, 199, 165),
        TrayColorTheme.Blue => Color.FromArgb(67, 144, 246),
        TrayColorTheme.Green => Color.FromArgb(70, 180, 103),
        TrayColorTheme.Purple => Color.FromArgb(167, 119, 238),
        TrayColorTheme.Orange => Color.FromArgb(237, 152, 53),
        _ => Normal
    };

    // Transparent, supersampled by Shell at the actual taskbar icon size.
    internal static Bitmap Draw(BatteryRings rings, int size = 64)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 64f, size / 64f);
        var color = NormalColor(rings.Theme);
        DrawRing(g, new RectangleF(5, 5, 54, 54), rings.Outer, color);
        DrawRing(g, new RectangleF(18, 18, 28, 28), rings.Inner, color);
        return bitmap;
    }

    private static void DrawRing(Graphics g, RectangleF bounds, BatteryRing ring, Color normal)
    {
        // A neutral hairline keeps the black remaining arc visible on dark taskbars.
        using var edge = new Pen(Color.FromArgb(210, 127, 133, 143), 7);
        if (ring.Percent is not int percent)
        {
            // Unknown/idle never looks like a full or empty battery.
            edge.DashPattern = [1.2f, 1.6f];
            using var unknown = new Pen(ring.Low ? Low : Unknown, 5) { DashPattern = [1.68f, 2.24f] };
            g.DrawEllipse(edge, bounds); g.DrawEllipse(unknown, bounds);
            return;
        }
        g.DrawEllipse(edge, bounds);
        // A fine red rim also makes an empty (0%) low battery visible as low.
        if (ring.Low) { using var rim = new Pen(Low, 7); g.DrawEllipse(rim, bounds); }
        using var consumed = new Pen(Consumed, 5);
        g.DrawEllipse(consumed, bounds);
        if (percent == 0) return;
        using var remaining = new Pen(ring.Low ? Low : normal, 5);
        if (percent == 100) g.DrawEllipse(remaining, bounds);
        else
        {
            float sweep = percent * 3.6f;
            // Remaining arc is symmetric around 12 o'clock, so consumed white
            // is always centered at 6 o'clock, independent of battery level.
            g.DrawArc(remaining, bounds, -90 - sweep / 2, sweep);
        }
    }

    internal static Icon Create(BatteryRings rings)
    {
        using var bitmap = Draw(rings);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { NativeUi.DestroyIcon(handle); }
    }
}
