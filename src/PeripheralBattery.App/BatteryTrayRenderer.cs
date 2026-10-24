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

internal readonly record struct BatteryRings(BatteryRing Outer, BatteryRing Inner)
{
    internal static BatteryRings From(Reading mouse, Reading keyboard, AppSettings settings)
    {
        var m = BatteryRing.From(mouse, settings.LowThreshold);
        var k = BatteryRing.From(keyboard, settings.LowThreshold);
        return settings.KeyboardOuterRing ? new(k, m) : new(m, k);
    }
}

internal static class BatteryTrayRenderer
{
    internal static readonly Color Normal = Color.FromArgb(54, 199, 165);
    internal static readonly Color Low = Color.FromArgb(243, 75, 80);
    internal static readonly Color Consumed = Color.FromArgb(250, 250, 252);
    internal static readonly Color Unknown = Color.FromArgb(145, 151, 161);

    // Transparent, supersampled by Shell at the actual taskbar icon size.
    internal static Bitmap Draw(BatteryRings rings, int size = 64)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 64f, size / 64f);
        DrawRing(g, new RectangleF(5, 5, 54, 54), rings.Outer);
        DrawRing(g, new RectangleF(18, 18, 28, 28), rings.Inner);
        return bitmap;
    }

    private static void DrawRing(Graphics g, RectangleF bounds, BatteryRing ring)
    {
        using var edge = new Pen(Color.FromArgb(190, 32, 35, 40), 8);
        if (ring.Percent is not int percent)
        {
            // Unknown/idle never looks like a full or empty battery.
            edge.DashPattern = [1.2f, 1.6f];
            using var unknown = new Pen(ring.Low ? Low : Unknown, 5) { DashPattern = [1.92f, 2.56f] };
            g.DrawEllipse(edge, bounds); g.DrawEllipse(unknown, bounds);
            return;
        }
        g.DrawEllipse(edge, bounds);
        // A fine red rim also makes an empty (0%) low battery visible as low.
        if (ring.Low) { using var rim = new Pen(Low, 7); g.DrawEllipse(rim, bounds); }
        using var consumed = new Pen(Consumed, 5);
        g.DrawEllipse(consumed, bounds);
        if (percent == 0) return;
        using var remaining = new Pen(ring.Low ? Low : Normal, 5);
        if (percent == 100) g.DrawEllipse(remaining, bounds);
        else g.DrawArc(remaining, bounds, -90, percent * 3.6f);
    }

    internal static Icon Create(BatteryRings rings)
    {
        using var bitmap = Draw(rings);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { NativeUi.DestroyIcon(handle); }
    }
}
