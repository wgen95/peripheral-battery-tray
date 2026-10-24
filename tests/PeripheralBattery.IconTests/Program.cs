using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Runtime.InteropServices;
using PeripheralBattery.App;
using PeripheralBattery.Core;

// Pure rendering/config tests: no tray registration, forms, HID or user settings writes.
int count = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
Reading Value(string name, int value) => new(name, "test", BatteryState.Valid, value, "fixture", "fixture", DateTimeOffset.Now);
var mouse = Value("A950", 75); var keyboard = Value("S75", 25);
var settings = new AppSettings();
var normal = BatteryRings.From(mouse, keyboard, settings);
Check(normal.Outer.Percent == 25 && normal.Inner.Percent == 75, "default outer keyboard / inner mouse");
settings.KeyboardOuterRing = false;
var swapped = BatteryRings.From(mouse, keyboard, settings);
Check(swapped.Outer.Percent == 75 && swapped.Inner.Percent == 25, "swapped outer mouse / inner keyboard");
Check(JsonSerializer.Deserialize<AppSettings>("{}", Json.Options)!.KeyboardOuterRing, "old settings preserve default keyboard outer");
Check(!JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json.Options), Json.Options)!.KeyboardOuterRing,
    "ring order survives settings serialization");
Check(JsonSerializer.Deserialize<AppSettings>("{}", Json.Options)!.ColorTheme == TrayColorTheme.Monochrome,
    "old settings default to monochrome");
var paletteSettings = new AppSettings { ColorTheme = TrayColorTheme.Purple };
Check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(paletteSettings, Json.Options), Json.Options)!.ColorTheme == TrayColorTheme.Purple,
    "palette survives settings serialization");
Check(BatteryRings.From(mouse, keyboard, paletteSettings) != normal, "palette changes invalidate the rendered icon cache");
Check(BatteryRing.From(Value("A950", 20), 20).Low, "threshold boundary is low");
Check(!BatteryRing.From(Value("A950", 21), 20).Low, "above threshold is normal");
Check(BatteryRing.From(Value("A950", 30), 40).Low, "custom threshold is used");
foreach (var state in Enum.GetValues<BatteryState>().Where(s => s != BatteryState.Valid))
    Check(BatteryRing.From(mouse with { State = state }, 20).Percent == null, "non-valid state never reuses a percentage");
Check(BatteryRing.From(mouse with { Percent = 101 }, 20).Percent == null, "invalid percentage never draws a progress ring");

bool Near(Color actual, Color expected) => Math.Abs(actual.R - expected.R) + Math.Abs(actual.G - expected.G) + Math.Abs(actual.B - expected.B) < 45 && actual.A > 200;
Color At(Bitmap image, double radius, double degrees)
{
    double radians = degrees * Math.PI / 180;
    return image.GetPixel((int)Math.Round(32 + radius * Math.Sin(radians)), (int)Math.Round(32 - radius * Math.Cos(radians)));
}
using (var image = BatteryTrayRenderer.Draw(normal))
{
    Check(Near(At(image, 27, 0), BatteryTrayRenderer.Normal), "outer remaining quarter is centered at top");
    Check(Near(At(image, 27, 135), BatteryTrayRenderer.Consumed), "outer consumed arc is white");
    Check(Near(At(image, 14, 90), BatteryTrayRenderer.Normal), "inner three-quarter progress is independent");
    Check(Near(At(image, 14, 180), BatteryTrayRenderer.Consumed), "inner consumed arc is centered at bottom");
    Check(image.GetPixel(32, 32).A == 0 && image.GetPixel(32, 11).A == 0, "center and gap stay transparent");
}
using (var image = BatteryTrayRenderer.Draw(new(new(10, true), new(100, false))))
{
    Check(Near(At(image, 27, 0), BatteryTrayRenderer.Low), "low outer arc is red");
    Check(Near(At(image, 27, 180), BatteryTrayRenderer.Consumed), "low consumed arc stays white");
    Check(Near(At(image, 14, 180), BatteryTrayRenderer.Normal), "one low battery does not turn the other ring red");
}
foreach (var theme in Enum.GetValues<TrayColorTheme>())
{
    using var image = BatteryTrayRenderer.Draw(new(new(50, false), new(25, true), theme));
    foreach (double angle in new[] { 45d, 315d })
        Check(Near(At(image, 27, angle), BatteryTrayRenderer.NormalColor(theme)), "normal half stays on both upper quadrants for every palette");
    foreach (double angle in new[] { 135d, 225d })
        Check(Near(At(image, 27, angle), BatteryTrayRenderer.Consumed), "white half stays on both lower quadrants for every palette");
    Check(Near(At(image, 14, 0), BatteryTrayRenderer.Low), "red overrides every selected palette for a low device");
}
foreach (int percent in new[] { 10, 25, 50, 75, 90 })
{
    using var image = BatteryTrayRenderer.Draw(new(new(percent, false), new(percent, false)));
    foreach (int radius in new[] { 14, 27 })
    {
        Check(Near(At(image, radius, 0), BatteryTrayRenderer.Normal), "remaining arc stays centered at top as the level changes");
        Check(Near(At(image, radius, 180), BatteryTrayRenderer.Consumed), "consumed arc stays centered at bottom as the level changes");
    }
}
using (var image = BatteryTrayRenderer.Draw(new(new(0, true), new(100, false))))
{
    Check(Near(At(image, 27, 90), BatteryTrayRenderer.Consumed), "0 percent has no remaining arc");
    Check(Near(At(image, 14, 270), BatteryTrayRenderer.Normal), "100 percent is a full colored ring");
    var rim = At(image, 30, 90);
    Check(rim.R > rim.G * 1.8 && rim.R > rim.B * 1.8 && rim.A > 50, "0 percent still has a fine low-battery rim");
}
using (var image = BatteryTrayRenderer.Draw(new(new(null, false), new(null, false))))
{
    Check(!Enumerable.Range(0, 64).SelectMany(x => Enumerable.Range(0, 64).Select(y => image.GetPixel(x, y)))
        .Any(c => Near(c, BatteryTrayRenderer.Normal) || Near(c, BatteryTrayRenderer.Consumed)), "unknown ring cannot resemble a measured full/empty ring");
}

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "evidence/tray-rings-preview.png");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var cases = new (string Title, BatteryRings Rings)[]
{
    ("S75 25% / A950 75%", normal),
    ("S75 10% / A950 75%", new(new(10, true), new(75, false))),
    ("S75 54% / A950 15%", new(new(54, false), new(15, true))),
    ("S75 100% / A950 0%", new(new(100, false), new(0, true))),
    ("S75 idle / A950 75%", new(new(null, false), new(75, false))),
    ("Swapped: A950 / S75", swapped)
};
using (var sheet = new Bitmap(780, 380))
using (var g = Graphics.FromImage(sheet))
using (var titleFont = new Font("Segoe UI", 12f))
using (var font = new Font("Segoe UI", 9f))
{
    g.Clear(Color.FromArgb(24, 26, 30));
    g.DrawString("DEMO fixtures - outer / inner - 16, 20, 24, 32, 48 px", titleFont, Brushes.White, 18, 12);
    for (int i = 0; i < cases.Length; i++)
    {
        int x = 18 + i % 3 * 260, y = 52 + i / 3 * 160;
        g.DrawString(cases[i].Title, font, Brushes.WhiteSmoke, x, y);
        foreach (var light in new[] { false, true })
        {
            int top = y + 26 + (light ? 57 : 0);
            using var background = new SolidBrush(light ? Color.FromArgb(235, 237, 241) : Color.FromArgb(38, 39, 43));
            g.FillRectangle(background, x, top, 244, 54);
            int offset = 8;
            foreach (int size in new[] { 16, 20, 24, 32, 64 })
            {
                using var icon = BatteryTrayRenderer.Draw(cases[i].Rings, size);
                if (size == 64) { g.DrawImage(icon, new Rectangle(x + offset, top + 3, 48, 48)); }
                else g.DrawImageUnscaled(icon, x + offset, top + (54 - size) / 2);
                Check(icon.GetPixel(0, 0).A == 0, "all icon sizes have a transparent exterior");
                offset += size == 64 ? 56 : size + 10;
            }
        }
    }
    sheet.Save(output);
}
Exception? settingsError = null;
var settingsThread = new Thread(() =>
{
    try
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        using var form = new SettingsForm(settings, [], _ => throw new Exception("preview must not save user settings"));
        var order = (ComboBox)form.Controls.Find("RingOrder", true).Single();
        Check(order.SelectedIndex == 1 && order.Items.Count == 2, "settings UI reflects swapped device order");
        var colors = (ComboBox)form.Controls.Find("TrayColors", true).Single();
        Check(colors.SelectedIndex == 0 && colors.Items.Count == 6, "settings UI exposes six palettes with monochrome default");
        // Render offscreen. Do not Show(), run the app, or change the installed tray.
        // A never-shown Form omits invisible children in DrawToBitmap. Render its
        // detached layout instead, with the same font and client dimensions.
        using var layout = (TableLayoutPanel)form.Controls[0];
        layout.Font = form.Font; form.Controls.Remove(layout); layout.Size = form.ClientSize;
        layout.CreateControl();
        foreach (Control control in layout.Controls) control.CreateControl();
        layout.PerformLayout();
        Check(layout.ClientRectangle.Contains(order.Bounds), "device order selector fits in the settings layout");
        Check(layout.ClientRectangle.Contains(colors.Bounds), "palette selector fits in the settings layout");
        Check(layout.ClientRectangle.Contains(((Control)form.AcceptButton!).Bounds), "save button fits in the settings layout");
        using var image = new Bitmap(layout.Width, layout.Height);
        layout.DrawToBitmap(image, layout.ClientRectangle);
        image.Save(Path.Combine(Path.GetDirectoryName(output)!, "ring-settings-preview.png"));
    }
    catch (Exception e) { settingsError = e; }
});
settingsThread.SetApartmentState(ApartmentState.STA); settingsThread.Start(); settingsThread.Join();
if (settingsError != null) throw settingsError;

using var process = System.Diagnostics.Process.GetCurrentProcess();
// Warm up native icon allocation, then check that repeated refreshes release handles.
using (var warmup = BatteryTrayRenderer.Create(normal)) { }
uint beforeGdi = Resources.GetGuiResources(process.Handle, 0), beforeUser = Resources.GetGuiResources(process.Handle, 1);
for (int i = 0; i < 250; i++) { using var icon = BatteryTrayRenderer.Create(i % 2 == 0 ? normal : swapped); }
Check(Resources.GetGuiResources(process.Handle, 0) <= beforeGdi + 2, "repeated icon creation must not leak GDI objects");
Check(Resources.GetGuiResources(process.Handle, 1) <= beforeUser + 2, "repeated icon creation must not leak USER icon handles");
Console.WriteLine($"PASS: {count} ring mapping, threshold, pixel color, unknown state, config and size assertions. DEMO preview: {output}");

static class Resources
{
    [DllImport("user32.dll")] internal static extern uint GetGuiResources(IntPtr process, uint flags);
}
