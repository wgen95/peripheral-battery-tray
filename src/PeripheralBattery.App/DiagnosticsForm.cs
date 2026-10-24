using System.Text;
using System.Text.Json;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal sealed class DiagnosticsForm : Form
{
    private readonly Label summary = new() { Dock = DockStyle.Top, Height = 88, Padding = new Padding(12), AutoEllipsis = true };
    private readonly ListBox devices = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, WordWrap = false, BorderStyle = BorderStyle.None };
    private readonly Label timestamp = new() { AutoSize = true, Margin = new Padding(12, 10, 6, 0) };
    private Snapshot? snapshot;
    private Dictionary<string, Reading> history = new();
    internal DiagnosticsForm(Action refresh)
    {
        Text = "无线外设电量 · 设备诊断"; Font = new Font("Microsoft YaHei UI", 9f); AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(840, 620); MinimumSize = new Size(640, 480); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(28, 30, 34); ForeColor = Color.FromArgb(233, 235, 240);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 170, BackColor = BackColor };
        devices.BackColor = Color.FromArgb(35, 38, 43); devices.ForeColor = ForeColor;
        details.BackColor = BackColor; details.ForeColor = ForeColor; details.Font = new Font("Consolas", 10);
        split.Panel1.Controls.Add(devices); split.Panel2.Controls.Add(details);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8), WrapContents = false };
        Button Button(string text, Action action)
        {
            var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Height = 30 }; b.Click += (_, _) => action(); return b;
        }
        buttons.Controls.Add(Button("刷新", refresh)); buttons.Controls.Add(Button("导出诊断", Export));
        buttons.Controls.Add(Button("查看日志", Storage.OpenLog)); buttons.Controls.Add(timestamp);
        Controls.Add(split); Controls.Add(summary); Controls.Add(buttons);
        Shown += (_, _) => split.SplitterDistance = Math.Clamp(160 * DeviceDpi / 96, split.Panel1MinSize, Math.Max(split.Panel1MinSize, split.Height - split.Panel2MinSize - split.SplitterWidth));
        devices.SelectedIndexChanged += (_, _) => ShowDetails();
    }
    internal void UpdateSnapshot(Snapshot value, Dictionary<string, Reading> validHistory)
    {
        snapshot = value; history = new(validHistory);
        summary.Text = $"{value.Mouse.Name}   {value.Mouse.Display}       {value.Keyboard.Name}   {value.Keyboard.Display}\n接收器在线不代表无线设备在线；历史值仅供诊断，不作为当前电量。";
        timestamp.Text = $"最近扫描 {value.CapturedAt:HH:mm:ss}";
        var selected = (devices.SelectedItem as HidDevice)?.Path;
        devices.BeginUpdate(); devices.Items.Clear();
        foreach (var d in value.Devices.OrderByDescending(d => d.Vid is 0x260D or 0x0C45).ThenBy(d => d.Id).ThenBy(d => d.UsagePage)) devices.Items.Add(d);
        devices.EndUpdate();
        if (devices.Items.Count > 0) devices.SelectedIndex = Math.Max(0, devices.Items.Cast<HidDevice>().ToList().FindIndex(d => d.Path == selected));
        else details.Text = BuildText();
    }
    private string BuildText()
    {
        if (snapshot == null) return "等待首次设备扫描…";
        var b = new StringBuilder();
        foreach (var r in new[] { snapshot.Mouse, snapshot.Keyboard })
        {
            b.AppendLine($"{r.Name} [{r.Category}] · {r.Display}"); b.AppendLine("读取方式: " + r.Method);
            b.AppendLine("尝试时间: " + r.AttemptedAt.ToString("O")); b.AppendLine("结果: " + r.Detail);
            b.AppendLine("真实电量: " + (r.State == BatteryState.Valid ? "本次已取得并验证设备报告" : "本次未取得有效百分比"));
            if (history.TryGetValue(r.Name, out var previous))
                b.AppendLine($"历史有效值（非当前结果）: {previous.Percent}% · {previous.AttemptedAt:yyyy-MM-dd HH:mm:ss} · 接口 {previous.ReceiverKey}");
            if (!string.IsNullOrEmpty(r.RawFrame)) b.AppendLine("原始响应: " + r.RawFrame);
            b.AppendLine();
        }
        b.AppendLine("排查: 唤醒外设 → 刷新 → 确认设置中的接收器 → 检查厂商软件是否占用通道。");
        b.AppendLine("其他型号需要独立适配。未解析厂商协议时，原因尚未确定。充电标志尚未验证。");
        return b.ToString();
    }
    private void ShowDetails()
    {
        var b = new StringBuilder(BuildText());
        if (devices.SelectedItem is HidDevice d)
        {
            b.AppendLine("──────────────── 所选 HID 接口 ────────────────");
            b.AppendLine($"名称: {d.Product}\n类别: {d.Category}\n制造商: {d.Manufacturer}\nVID:PID: {d.Id}\nbcdDevice: {d.Version:X4}\nUsage: {d.UsagePage:X4}:{d.Usage:X4}");
            b.AppendLine($"输入 / 输出 / Feature 字节数: {d.InputLength} / {d.OutputLength} / {d.FeatureLength}");
            b.AppendLine("HID 路径: " + d.Path); b.AppendLine("实例: " + d.InstanceId); b.AppendLine("USB 父接口: " + d.ReceiverKey);
            b.AppendLine("连接: 本次枚举存在（仅接口在线）");
            b.AppendLine("枚举错误: " + (d.Error ?? "无"));
            b.AppendLine("声明的 Value Usage（类型 0=Input / 1=Output / 2=Feature）:");
            foreach (var v in d.Values) b.AppendLine($"  类型 {v.ReportType} · Report {v.ReportId:X2} · {v.Page:X4}:{v.UsageMin:X4}..{v.UsageMax:X4} · 范围 {v.LogicalMin}..{v.LogicalMax}");
            bool battery = d.Values.Any(v => v.Page == 6 && v.UsageMin <= 0x20 && v.UsageMax >= 0x20 || v.Page == 0x85);
            b.AppendLine(battery ? "发现潜在标准电池 Usage，尚未为此接口实现并验证读取。" : "此集合未发现标准 Battery Strength / Battery System Value Usage；可能使用私有协议。");
            b.AppendLine("不将未知字节、信号强度、低电量标志转换为百分比。");
        }
        details.Text = b.ToString(); details.SelectionStart = 0;
    }
    private void Export()
    {
        if (snapshot == null) return;
        using var dialog = new SaveFileDialog { Filter = "JSON 诊断|*.json", FileName = $"battery-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { Snapshot = snapshot, LastValidReadings = history }, Json.Options));
    }
}
