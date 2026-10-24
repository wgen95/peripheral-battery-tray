using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeripheralBattery.Core;

public enum BatteryState { Valid, Unknown, Disconnected, WirelessDisconnected, Sleeping, ReadFailed, Unsupported, LowBatteryWarning, Idle }
public sealed record Reading(string Name, string Category, BatteryState State, int? Percent,
    string Method, string Detail, DateTimeOffset AttemptedAt, string? ReceiverKey = null, string? RawFrame = null)
{
    public string Display => State switch
    {
        BatteryState.Valid when Percent is >= 0 and <= 100 => $"{Percent}%",
        BatteryState.Disconnected => "接收器断连",
        BatteryState.WirelessDisconnected => "设备断连",
        BatteryState.Sleeping => "休眠中",
        BatteryState.Idle => "待使用",
        BatteryState.ReadFailed => "读取失败",
        BatteryState.Unsupported => "暂不支持",
        BatteryState.LowBatteryWarning => "低电量警告",
        _ => "电量未知"
    };
    public static Reading Empty(string name, string category, BatteryState state, string detail) =>
        new(name, category, state, null, "未采用读取接口", detail, DateTimeOffset.Now);
}
public sealed record ValueUsage(int ReportType, byte ReportId, ushort Page, ushort UsageMin,
    ushort UsageMax, int LogicalMin, int LogicalMax);
public sealed record HidDevice(string Path, string InstanceId, string ReceiverKey, string Product,
    string Manufacturer, ushort Vid, ushort Pid, ushort Version, ushort UsagePage, ushort Usage,
    ushort InputLength, ushort OutputLength, ushort FeatureLength, List<ValueUsage> Values, string? Error)
{
    public string Id => $"{Vid:X4}:{Pid:X4}";
    public string Category => UsagePage == 1 && Usage == 6 ? "键盘接口" :
        UsagePage == 1 && Usage == 2 ? "鼠标接口" : UsagePage >= 0xFF00 ? "厂商 HID 接口" : "HID 接口";
    public override string ToString() => $"{Product} · {Id} · {UsagePage:X4}:{Usage:X4}";
}
public sealed record Snapshot(DateTimeOffset CapturedAt, List<HidDevice> Devices, Reading Mouse, Reading Keyboard);
public sealed class AppSettings
{
    public int PollSeconds { get; set; } = 60;
    public int LowThreshold { get; set; } = 20;
    public bool HoverPanel { get; set; } = true;
    public bool KeyboardOuterRing { get; set; } = true;
    public string? MouseReceiver { get; set; }
    public string? KeyboardReceiver { get; set; }
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
