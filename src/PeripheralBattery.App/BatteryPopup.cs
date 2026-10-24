using System.Drawing.Drawing2D;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal sealed class BatteryPopup : Form
{
    private Reading[] rows = [Reading.Empty("鼠标 A950", "鼠标", BatteryState.Unknown, "等待读取"), Reading.Empty("键盘 S75", "键盘", BatteryState.Unknown, "等待读取")];
    private readonly System.Windows.Forms.Timer leaveTimer = new() { Interval = 100 };
    private bool pinned;
    private long outsideSince;
    private Point? dismissedAt;
    internal int LowThreshold { get; set; } = 20;
    internal bool IsPinned => pinned;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000080; p.ClassStyle |= 0x20000; return p; }
    }
    internal BatteryPopup()
    {
        Text = "无线外设电量"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        BackColor = Color.FromArgb(38, 38, 40); ForeColor = Color.FromArgb(246, 246, 247);
        Font = new Font("Microsoft YaHei UI", 10f); DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(220, 72);
        leaveTimer.Tick += (_, _) =>
        {
            if (!Visible) return;
            var point = Cursor.Position;
            if (NativeUi.GetAsyncKeyState(0x1B) < 0) { Dismiss(); return; }
            if (!Bounds.Contains(point) && NativeUi.GetAsyncKeyState(1) < 0) { Dismiss(); return; }
            if (pinned || Bounds.Contains(point)) { outsideSince = 0; return; }
            if (outsideSince == 0) outsideSince = Environment.TickCount64;
            if (Environment.TickCount64 - outsideSince > 1400) Dismiss();
        };
        MouseDown += (_, _) => Dismiss();
    }
    internal void SetReadings(Reading mouse, Reading keyboard)
    { rows = [mouse, keyboard]; Invalidate(); }
    internal void OpenNear(Point anchor, bool pin)
    {
        if (!pin && dismissedAt == anchor) return;
        if (Visible)
        {
            if (pin && pinned) { Dismiss(); return; }
            pinned |= pin; outsideSince = 0; return;
        }
        dismissedAt = null; pinned = pin; outsideSince = 0;
        var area = Screen.FromPoint(anchor).WorkingArea;
        Location = new Point(Math.Clamp(anchor.X - Width / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(anchor.Y - Height - 12, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        Show();
        ClientSize = new Size(220 * DeviceDpi / 96, 72 * DeviceDpi / 96);
        // Moving to a different monitor can change DPI and size during Show().
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        leaveTimer.Start();
    }
    internal void Dismiss() { dismissedAt = Cursor.Position; Hide(); leaveTimer.Stop(); pinned = false; }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ClientSize = new Size(220 * DeviceDpi / 96, 72 * DeviceDpi / 96);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        { int corner = 2; NativeUi.DwmSetWindowAttribute(Handle, 33, ref corner, 4); }
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ClientSize = new Size(220 * e.DeviceDpiNew / 96, 72 * e.DeviceDpiNew / 96);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); float scale = DeviceDpi / 96f;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(109, 109, 111), scale);
        using var path = new GraphicsPath();
        float radius = 6 * scale, right = ClientSize.Width - 1, bottom = ClientSize.Height - 1;
        path.AddArc(0, 0, radius * 2, radius * 2, 180, 90); path.AddArc(right - radius * 2, 0, radius * 2, radius * 2, 270, 90);
        path.AddArc(right - radius * 2, bottom - radius * 2, radius * 2, radius * 2, 0, 90); path.AddArc(0, bottom - radius * 2, radius * 2, radius * 2, 90, 90); path.CloseFigure(); g.DrawPath(pen, path);
        int pad = (int)(13 * scale), top = (int)(11 * scale), rowHeight = (int)(25 * scale), statusWidth = (int)(100 * scale);
        for (int i = 0; i < 2; i++)
        {
            var row = rows[i]; var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, row.Name, Font, new Rectangle(pad, top + i * rowHeight, ClientSize.Width - pad * 2 - statusWidth, rowHeight), ForeColor, flags);
            var color = row.State == BatteryState.Valid ? row.Percent <= LowThreshold ? Color.FromArgb(255, 171, 111) : ForeColor : Color.FromArgb(185, 187, 194);
            TextRenderer.DrawText(g, row.Display, Font, new Rectangle(ClientSize.Width - pad - statusWidth, top + i * rowHeight, statusWidth, rowHeight), color, flags | TextFormatFlags.Right);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) leaveTimer.Dispose(); base.Dispose(disposing); }
}
