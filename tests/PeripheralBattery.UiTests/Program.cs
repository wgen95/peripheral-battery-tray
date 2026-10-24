using System.Reflection;
using System.Runtime.InteropServices;
using PeripheralBattery.App;
using PeripheralBattery.Core;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct Identifier
    { public uint Size; public IntPtr Hwnd; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rect rect);
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
        var snapshot = Probe.RunAsync(new(), CancellationToken.None).GetAwaiter().GetResult();
        using var popup = new BatteryPopup(); popup.SetReadings(snapshot.Mouse, snapshot.Keyboard);
        var area = Screen.PrimaryScreen!.WorkingArea;
        popup.OpenNear(new Point(area.Right, area.Bottom), true); Application.DoEvents();
        var handle = popup.Handle;
        for (int i = 0; i < 50; i++)
        {
            popup.Dismiss(); popup.OpenNear(new Point(area.Right, area.Bottom), true); Application.DoEvents();
            if (popup.Handle != handle || !area.Contains(popup.Bounds)) throw new Exception("popup lifecycle/bounds");
        }
        using var bitmap = new Bitmap(popup.Width, popup.Height); popup.DrawToBitmap(bitmap, popup.ClientRectangle);
        var path = Path.GetFullPath(args.Length > 0 ? args[0] : "panel-preview.png"); bitmap.Save(path);
        popup.Dismiss();
        using var diagnostic = new DiagnosticsForm(() => { });
        diagnostic.UpdateSnapshot(new(DateTimeOffset.Now, [], snapshot.Mouse, snapshot.Keyboard), new());
        diagnostic.UpdateSnapshot(snapshot, new()); diagnostic.Show(); Application.DoEvents();
        using (var shot = new Bitmap(diagnostic.Width, diagnostic.Height)) { diagnostic.DrawToBitmap(shot, new Rectangle(Point.Empty, diagnostic.Size)); shot.Save(Path.Combine(Path.GetDirectoryName(path)!, "diagnostics-preview.png")); }
        diagnostic.Close();
        using var settings = new SettingsForm(new(), snapshot.Devices, _ => { }); settings.Show(); Application.DoEvents();
        using (var shot = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(shot, new Rectangle(Point.Empty, settings.Size)); shot.Save(Path.Combine(Path.GetDirectoryName(path)!, "settings-preview.png")); }
        settings.Close();
        using var context = new TrayContext(false, false, false, startMonitoring: false); Application.DoEvents();
        var tray = (TrayIcon)typeof(TrayContext).GetField("tray", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!;
        var id = new Identifier { Size = (uint)Marshal.SizeOf<Identifier>(), Hwnd = tray.Handle, Id = 1, Guid = TrayIcon.Identity };
        int result = Shell_NotifyIconGetRect(ref id, out var rect);
        if (result != 0) throw new Exception($"Shell tray icon not registered: {result:X8}");
        var trayPopup = (BatteryPopup)typeof(TrayContext).GetField("popup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!;
        if (tray.ShowNativeTip) throw new Exception("native tooltip would overlap custom hover panel");
        tray.Dispatch(0x400, Cursor.Position); Application.DoEvents();
        if (!trayPopup.Visible) throw new Exception("left click did not open popup");
        tray.Dispatch(0x400, Cursor.Position); Application.DoEvents();
        if (trayPopup.Visible || !tray.Visible) throw new Exception("close popup stopped background tray");
        tray.Dispatch(0x201, new Point(20, 20)); tray.Dispatch(0x200, new Point(80, 80)); tray.Dispatch(0x400, new Point(80, 80));
        Application.DoEvents();
        if (trayPopup.Visible) throw new Exception("drag opened a popup / blocked moving the tray icon");
        tray.Dispatch(0x401, Cursor.Position); Application.DoEvents();
        if (!trayPopup.Visible) throw new Exception("keyboard activation did not open panel");
        trayPopup.Dismiss();
        tray.Dispatch(0x200, Cursor.Position); tray.Dispatch(0x406, Cursor.Position);
        var hoverDeadline = DateTime.UtcNow.AddMilliseconds(650);
        while (DateTime.UtcNow < hoverDeadline) { Application.DoEvents(); Thread.Sleep(10); }
        if (!trayPopup.Visible || tray.ShowNativeTip) throw new Exception("Shell hover did not show exactly the custom panel");
        trayPopup.Dismiss(); tray.Dispatch(0x407, Cursor.Position);
        var appSettings = (AppSettings)typeof(TrayContext).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!;
        appSettings.HoverPanel = false; tray.ShowNativeTip = true;
        tray.Dispatch(0x401, Cursor.Position); Application.DoEvents();
        if (tray.ShowNativeTip) throw new Exception("native tooltip overlaps a clicked panel when hover mode is disabled");
        trayPopup.Dismiss();
        if (!tray.ShowNativeTip) throw new Exception("native fallback tooltip was not restored");
        var menu = (ContextMenuStrip)typeof(TrayContext).GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!;
        tray.Dispatch(0x7B, Cursor.Position); Application.DoEvents();
        if (!menu.Visible) throw new Exception("native context-menu callback did not open menu");
        menu.Close();
        ((ToolStripMenuItem)menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "设备诊断")).PerformClick(); Application.DoEvents();
        var diagnosticWindow = (DiagnosticsForm)typeof(TrayContext).GetField("diagnostics", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!;
        diagnosticWindow.Close(); typeof(TrayContext).GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(context, null);
        Console.WriteLine($"PASS: 50 popup cycles, stable HWND, screen bounds, diagnostic/settings UI, GUID Shell registration, click/hover/context-menu callbacks, tooltip exclusivity, drag suppression and refresh after diagnostic close. Preview {bitmap.Width}x{bitmap.Height}. Real readings: {snapshot.Mouse.Display} / {snapshot.Keyboard.Display}.");
    }
}
