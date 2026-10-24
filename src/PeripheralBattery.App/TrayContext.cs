using System.Drawing.Drawing2D;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly TrayIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly BatteryPopup popup = new();
    private readonly EventWindow events = new();
    private readonly CommandChannel commands;
    private readonly System.Windows.Forms.Timer poll = new();
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 1800 };
    private readonly System.Windows.Forms.Timer hover = new() { Interval = 420 };
    private readonly System.Windows.Forms.Timer idle = new() { Interval = 1000 };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? scanCancellation;
    private Task? scan;
    private AppSettings settings = Storage.Load();
    private DiagnosticsForm? diagnostics;
    private SettingsForm? settingsForm;
    private Snapshot current = new(DateTimeOffset.Now, [], Reading.Empty("A950", "鼠标", BatteryState.Unknown, "等待读取"), Reading.Empty("S75", "键盘", BatteryState.Unknown, "等待读取"));
    private readonly Dictionary<string, Reading> history = new();
    private Point hoverPoint;
    private DateTimeOffset? manualKeyboardUntil;
    private bool closing, suspended, disposed;

    internal TrayContext(bool show, bool showDiagnostics, bool showSettings, bool startMonitoring = true)
    {
        Storage.Log("启动 v1.2.0 · 闲置键盘停止查询");
        popup.LowThreshold = settings.LowThreshold;
        tray = new TrayIcon(CreateIcon()) { Text = "A950 · S75", ContextMenu = menu, ShowNativeTip = !settings.HoverPanel };
        popup.VisibleChanged += (_, _) => tray.ShowNativeTip = !settings.HoverPanel && !popup.Visible;
        menu.Items.Add("显示电量", null, (_, _) => ShowPopup(true));
        menu.Items.Add("立即刷新", null, (_, _) => { manualKeyboardUntil = DateTimeOffset.Now + KeyboardActivity.ActiveWindow; RequestRefresh(); });
        menu.Items.Add("设备诊断", null, (_, _) => ShowDiagnostics());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("查看日志", null, (_, _) => Storage.OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await Shutdown());
        tray.Selected += _ => { hover.Stop(); ShowPopup(true); };
        tray.PointerPressed += () => { hover.Stop(); if (!popup.IsPinned) popup.Dismiss(); };
        tray.HoverClosed += () => hover.Stop();
        tray.PointerMoved += point =>
        {
            if (Control.MouseButtons != MouseButtons.None) { hover.Stop(); popup.Dismiss(); return; }
            if (!settings.HoverPanel || popup.Visible || menu.Visible) return;
            if (hover.Enabled && Math.Abs(hoverPoint.X - point.X) + Math.Abs(hoverPoint.Y - point.Y) < 3) return;
            hoverPoint = point; hover.Stop(); hover.Start();
        };
        hover.Tick += (_, _) =>
        {
            hover.Stop();
            if (Cursor.Position == hoverPoint && !menu.Visible && Control.MouseButtons == MouseButtons.None) ShowPopup(false);
        };
        menu.Opening += (_, _) => { hover.Stop(); if (!popup.IsPinned) popup.Dismiss(); };
        poll.Interval = settings.PollSeconds * 1000; poll.Tick += (_, _) => RequestRefresh(); if (startMonitoring) poll.Start();
        debounce.Tick += (_, _) => { debounce.Stop(); RequestRefresh(); };
        events.RefreshRequested += () => { suspended = false; debounce.Stop(); debounce.Start(); };
        events.KeyboardResumed += () => { if (!debounce.Enabled) debounce.Start(); };
        idle.Tick += (_, _) =>
        {
            if (!(KeyboardQueryUntil > DateTimeOffset.Now) && current.Keyboard.State == BatteryState.Valid)
            {
                current = current with { Keyboard = IdleKeyboard() }; Render();
            }
        };
        if (startMonitoring) idle.Start();
        events.SuspendRequested += () =>
        {
            suspended = true; manualKeyboardUntil = null; scanCancellation?.Cancel();
            current = current with { Mouse = Unknown(current.Mouse, "系统休眠，等候恢复读取"), Keyboard = Unknown(current.Keyboard, "系统休眠，等候恢复读取") };
            Render();
        };
        commands = new CommandChannel(popup, async command =>
        {
            if (command == 3) await Shutdown();
            else if (command == 1) ShowDiagnostics();
            else if (command == 2) ShowSettings();
            else ShowPopup(true);
        });
        if (startMonitoring) RequestRefresh();
        if (show) ShowPopup(true);
        if (showDiagnostics) ShowDiagnostics();
        if (showSettings) ShowSettings();
    }
    private static Reading Unknown(Reading r, string detail) => r with { State = BatteryState.Unknown, Percent = null, Detail = detail };
    private static Reading IdleKeyboard() => Reading.Empty("S75", "键盘", BatteryState.Idle, "键盘闲置，暂停查询以免干扰熄灯和休眠；按键后恢复。");
    private DateTimeOffset? KeyboardQueryUntil => events.Keyboard.QueryUntil > (manualKeyboardUntil ?? DateTimeOffset.MinValue)
        ? events.Keyboard.QueryUntil : manualKeyboardUntil;
    private void ShowPopup(bool pin)
    {
        var bounds = tray.Bounds;
        var point = bounds is Rectangle rect ? new Point(rect.Left + rect.Width / 2, rect.Top) : Cursor.Position;
        // Keyboard/second-instance activation may happen away from the taskbar.
        var area = Screen.FromPoint(point).WorkingArea;
        if (area.Contains(point) && point.Y < area.Bottom - 100 && point.X < area.Right - 100)
            point = new Point(area.Right - 110, area.Bottom);
        popup.OpenNear(point, pin);
    }
    private void RequestRefresh()
    {
        if (closing || suspended || scan is { IsCompleted: false }) return;
        scan = RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        scanCancellation?.Dispose(); scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = scanCancellation.Token;
        try
        {
            var next = await WorkerClient.ReadAsync(settings, ct, KeyboardQueryUntil);
            if (closing || ct.IsCancellationRequested) return;
            if (!(KeyboardQueryUntil > DateTimeOffset.Now) && next.Keyboard.State == BatteryState.Valid)
                next = next with { Keyboard = IdleKeyboard() };
            foreach (var r in new[] { next.Mouse, next.Keyboard })
            {
                if (r.State == BatteryState.Valid) history[r.Name] = r;
                var old = r.Name == current.Mouse.Name ? current.Mouse : current.Keyboard;
                if (old.State != r.State || old.Percent != r.Percent || old.ReceiverKey != r.ReceiverKey)
                    Storage.Log($"{r.Name}: {r.Display} · {r.Detail}");
            }
            current = next; Render();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closing) return;
            Storage.Log("刷新失败: " + ex.Message);
            current = current with
            {
                Mouse = current.Mouse with { State = BatteryState.ReadFailed, Percent = null, AttemptedAt = DateTimeOffset.Now, Detail = ex.Message },
                Keyboard = current.Keyboard with { State = BatteryState.ReadFailed, Percent = null, AttemptedAt = DateTimeOffset.Now, Detail = ex.Message }
            };
            Render();
        }
    }
    private void Render()
    {
        popup.SetReadings(current.Mouse, current.Keyboard);
        string tip = $"{current.Mouse.Name} {current.Mouse.Display}\n{current.Keyboard.Name} {current.Keyboard.Display}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        if (diagnostics is { IsDisposed: false }) diagnostics.UpdateSnapshot(current, history);
    }
    private void ShowDiagnostics()
    {
        popup.Dismiss();
        if (diagnostics is { IsDisposed: false }) { diagnostics.Activate(); return; }
        diagnostics = new DiagnosticsForm(RequestRefresh);
        diagnostics.UpdateSnapshot(current, history); diagnostics.Show();
    }
    private void ShowSettings()
    {
        popup.Dismiss();
        if (settingsForm is { IsDisposed: false }) { settingsForm.Activate(); return; }
        settingsForm = new SettingsForm(settings, current.Devices, updated =>
        {
            settings = updated; poll.Interval = settings.PollSeconds * 1000; popup.LowThreshold = settings.LowThreshold;
            tray.ShowNativeTip = !settings.HoverPanel && !popup.Visible; popup.Invalidate(); RequestRefresh();
        });
        settingsForm.Show();
    }
    private async Task Shutdown()
    {
        if (closing) return;
        closing = true; poll.Stop(); debounce.Stop(); hover.Stop(); idle.Stop(); tray.Visible = false; popup.Dismiss(); lifetime.Cancel();
        if (scan != null) await scan;
        Storage.Log("正常退出，查询任务已结束"); ExitThread();
    }
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shadow = new Pen(Color.FromArgb(160, 35, 35, 38), 5.5f);
        using var outline = new Pen(Color.FromArgb(245, 245, 247), 2.8f);
        foreach (var r in new[] { new RectangleF(8, 8, 48, 48), new RectangleF(22, 22, 20, 20) })
        {
            g.DrawEllipse(shadow, r); g.DrawEllipse(outline, r);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { NativeUi.DestroyIcon(handle); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            lifetime.Cancel(); tray.Visible = false; tray.Dispose();
            menu.Dispose(); popup.Dispose(); diagnostics?.Dispose(); settingsForm?.Dispose();
            commands.Dispose(); poll.Dispose(); debounce.Dispose(); hover.Dispose(); idle.Dispose(); events.Dispose(); scanCancellation?.Dispose(); lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
