using System.Drawing.Drawing2D;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly BatteryPopup popup = new();
    private readonly EventWindow events = new();
    private readonly CommandChannel commands;
    private readonly System.Windows.Forms.Timer poll = new();
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 1800 };
    private readonly System.Windows.Forms.Timer hover = new() { Interval = 420 };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? scanCancellation;
    private Task? scan;
    private AppSettings settings = Storage.Load();
    private DiagnosticsForm? diagnostics;
    private SettingsForm? settingsForm;
    private Snapshot current = new(DateTimeOffset.Now, [], Reading.Empty("鼠标 A950", "鼠标", BatteryState.Unknown, "等待读取"), Reading.Empty("键盘 S75", "键盘", BatteryState.Unknown, "等待读取"));
    private readonly Dictionary<string, Reading> history = new();
    private Point hoverPoint;
    private bool closing, suspended, disposed;

    internal TrayContext(bool show, bool showDiagnostics, bool showSettings, bool startMonitoring = true)
    {
        Storage.Log("启动 v1.0.0");
        popup.LowThreshold = settings.LowThreshold;
        tray = new NotifyIcon { Icon = CreateIcon(), Text = "无线外设电量 · 正在读取", ContextMenuStrip = menu, Visible = true };
        menu.Items.Add("显示电量", null, (_, _) => ShowPopup(true));
        menu.Items.Add("立即刷新", null, (_, _) => RequestRefresh());
        menu.Items.Add("设备诊断", null, (_, _) => ShowDiagnostics());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("查看日志", null, (_, _) => Storage.OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await Shutdown());
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { hover.Stop(); ShowPopup(true); } };
        tray.MouseMove += (_, _) =>
        {
            if (!settings.HoverPanel || popup.Visible || menu.Visible) return;
            if (hover.Enabled && Math.Abs(hoverPoint.X - Cursor.Position.X) + Math.Abs(hoverPoint.Y - Cursor.Position.Y) < 3) return;
            hoverPoint = Cursor.Position; hover.Stop(); hover.Start();
        };
        hover.Tick += (_, _) =>
        {
            hover.Stop();
            if (Cursor.Position == hoverPoint && !menu.Visible) popup.OpenNear(hoverPoint, false);
        };
        menu.Opening += (_, _) => { hover.Stop(); if (!popup.IsPinned) popup.Dismiss(); };
        poll.Interval = settings.PollSeconds * 1000; poll.Tick += (_, _) => RequestRefresh(); if (startMonitoring) poll.Start();
        debounce.Tick += (_, _) => { debounce.Stop(); RequestRefresh(); };
        events.RefreshRequested += () => { suspended = false; debounce.Stop(); debounce.Start(); };
        events.SuspendRequested += () =>
        {
            suspended = true; scanCancellation?.Cancel();
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
    private void ShowPopup(bool pin)
    {
        var point = Cursor.Position;
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
            var next = await WorkerClient.ReadAsync(settings, ct);
            if (closing || ct.IsCancellationRequested) return;
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
            popup.Invalidate(); RequestRefresh();
        });
        settingsForm.Show();
    }
    private async Task Shutdown()
    {
        if (closing) return;
        closing = true; poll.Stop(); debounce.Stop(); hover.Stop(); tray.Visible = false; popup.Dismiss(); lifetime.Cancel();
        if (scan != null) await scan;
        Storage.Log("正常退出，查询任务已结束"); ExitThread();
    }
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = new Pen(Color.FromArgb(240, 242, 244), 2.2f);
        using var dark = new SolidBrush(Color.FromArgb(42, 46, 52));
        using var teal = new SolidBrush(Color.FromArgb(109, 227, 196));
        for (int i = 0; i < 2; i++)
        {
            var r = new Rectangle(3, 4 + i * 13, 24, 9); g.FillRectangle(dark, r); g.DrawRectangle(outline, r);
            g.FillRectangle(teal, 6, r.Y + 3, i == 0 ? 17 : 12, 3); g.FillRectangle(dark, 28, r.Y + 2, 2, 5);
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
            lifetime.Cancel(); tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose();
            menu.Dispose(); popup.Dispose(); diagnostics?.Dispose(); settingsForm?.Dispose();
            commands.Dispose(); poll.Dispose(); debounce.Dispose(); hover.Dispose(); events.Dispose(); scanCancellation?.Dispose(); lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
