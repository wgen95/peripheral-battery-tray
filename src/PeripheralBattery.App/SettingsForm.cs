using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal sealed class SettingsForm : Form
{
    private sealed record ReceiverChoice(string Label, string? Key) { public override string ToString() => Label; }
    internal SettingsForm(AppSettings current, List<HidDevice> devices, Action<AppSettings> saved)
    {
        Text = "无线外设电量 · 设置"; Font = new Font("Microsoft YaHei UI", 9f); AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(560, 409); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 9 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var poll = new NumericUpDown { Minimum = 15, Maximum = 600, Value = current.PollSeconds, Increment = 15, Dock = DockStyle.Left, Width = 100 };
        var low = new NumericUpDown { Minimum = 1, Maximum = 50, Value = current.LowThreshold, Dock = DockStyle.Left, Width = 100 };
        var startup = new CheckBox { Text = "登录 Windows 时自动启动", AutoSize = true, Checked = Storage.AutoStart };
        var hover = new CheckBox { Text = "悬停显示紧凑浮层", AutoSize = true, Checked = current.HoverPanel };
        var ringOrder = new ComboBox { Name = "RingOrder", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        ringOrder.Items.AddRange(["外环 S75 · 内环 A950", "外环 A950 · 内环 S75"]);
        ringOrder.SelectedIndex = current.KeyboardOuterRing ? 0 : 1;
        ComboBox Receivers(IEnumerable<HidDevice> list, string? selected)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            box.Items.Add(new ReceiverChoice("自动选择（多个候选时不猜测）", null));
            foreach (var d in list.GroupBy(d => d.ReceiverKey).Select(g => g.First())) box.Items.Add(new ReceiverChoice($"{d.Id} · {d.Product} · {d.ReceiverKey.Split('\\').Last()}", d.ReceiverKey));
            if (selected != null && !box.Items.Cast<ReceiverChoice>().Any(c => c.Key == selected)) box.Items.Add(new ReceiverChoice("已保存的接口（当前不在线）", selected));
            box.SelectedIndex = Math.Max(0, box.Items.Cast<ReceiverChoice>().ToList().FindIndex(c => c.Key == selected)); return box;
        }
        var mouse = Receivers(devices.Where(d => d.Vid == 0x260d && d.UsagePage == 0xff02), current.MouseReceiver);
        var keyboard = Receivers(devices.Where(d => d.Vid == 0x0c45 && d.UsagePage == 0xff60), current.KeyboardReceiver);
        void Row(int index, string text, Control control)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            table.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, index); table.Controls.Add(control, 1, index);
        }
        Row(0, "间隔 / 秒", poll); Row(1, "低电量 / %", low); Row(2, "鼠标接收器", mouse); Row(3, "键盘接收器", keyboard);
        Row(4, "内外环设备", ringOrder);
        table.Controls.Add(startup, 1, 5); table.Controls.Add(hover, 1, 6);
        var hint = new Label { Text = "键盘闲置 30 秒后暂停查询，按键后恢复。\n“立即刷新”会主动查询，可能唤醒设备。", AutoSize = true, MaximumSize = new Size(510, 0), ForeColor = Color.DimGray };
        table.Controls.Add(hint, 0, 7); table.SetColumnSpan(hint, 2);
        var save = new Button { Text = "保存", AutoSize = true, Anchor = AnchorStyles.Right };
        table.Controls.Add(save, 1, 8); Controls.Add(table); AcceptButton = save;
        save.Click += (_, _) =>
        {
            var updated = new AppSettings { PollSeconds = (int)poll.Value, LowThreshold = (int)low.Value, HoverPanel = hover.Checked, KeyboardOuterRing = ringOrder.SelectedIndex == 0,
                MouseReceiver = ((ReceiverChoice)mouse.SelectedItem!).Key, KeyboardReceiver = ((ReceiverChoice)keyboard.SelectedItem!).Key };
            Storage.AutoStart = startup.Checked; Storage.Save(updated); saved(updated); Close();
        };
    }
}
