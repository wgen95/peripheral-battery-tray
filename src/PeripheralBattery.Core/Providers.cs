using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PeripheralBattery.Core;

public interface IBatteryProvider
{
    Task<Reading> ReadAsync(List<HidDevice> devices, string? selectedReceiver, CancellationToken ct);
}
internal static class Transport
{
    public static async Task<(int? Percent, string Frames)> Query(HidDevice input, HidDevice output,
        byte[] request, bool feature, Func<byte[], int?> parse, CancellationToken ct)
    {
        using var h = Native.Open(input.Path, async: true);
        using var stream = new FileStream(h, FileAccess.Read, 1, isAsync: true);
        Native.HidD_FlushQueue(h);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(1200);
        var buffer = new byte[input.InputLength];
        // Arm the input channel before sending the battery query.
        var pending = stream.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask();
        try
        {
            if (feature)
            {
                using var command = Native.Open(output.Path);
                if (!Native.HidD_SetFeature(command, request, request.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else
            {
                using var outputHandle = Native.Open(output.Path, async: true);
                using var writer = new FileStream(outputHandle, FileAccess.Write, 1, isAsync: true);
                await writer.WriteAsync(request, timeout.Token);
            }
            var frames = new List<string>();
            while (true)
            {
                int got;
                try { got = await pending; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { return (null, string.Join(" | ", frames)); }
                if (got == 0) return (null, string.Join(" | ", frames));
                var frame = buffer.AsSpan(0, got).ToArray();
                if (frames.Count < 8) frames.Add(Convert.ToHexString(frame));
                if (parse(frame) is int percent) return (percent, string.Join(" | ", frames));
                pending = stream.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return (null, "查询超时"); }
        finally
        {
            timeout.Cancel();
            // Drain cancelled I/O before freeing its buffer/handle.
            try { await pending; } catch (Exception) { }
        }
    }
}

public sealed class DareuA950Provider : IBatteryProvider
{
    public async Task<Reading> ReadAsync(List<HidDevice> devices, string? selectedReceiver, CancellationToken ct)
    {
        const string method = "A950 / Compx HID · 04 查询 + 校验和";
        var all = devices.Where(d => d.Vid == 0x260D && (d.Pid == 0x1074 || d.Pid == 0x1084)).ToList();
        var commands = all.Where(d => d.UsagePage == 0xFF02 && d.Usage == 2 && d.FeatureLength == 17 &&
            (selectedReceiver != null ? d.ReceiverKey == selectedReceiver : d.Pid == 0x1074)).ToList();
        if (commands.Count == 0) return Reading.Empty("鼠标 A950", "鼠标", all.Count == 0 || selectedReceiver != null ? BatteryState.Disconnected : BatteryState.Unsupported,
            "未找到选定的 A950 查询接口。1084 需在设置中显式选择，不能只凭通用产品名推断型号。");
        if (commands.Count > 1) return Reading.Empty("鼠标 A950", "鼠标", BatteryState.Unknown, "发现多个候选接收器，请在设置中选择目标。");
        var command = commands[0];
        var replies = all.Where(d => d.ReceiverKey == command.ReceiverKey && d.UsagePage == 0xFF01 && d.Usage == 0 && d.InputLength == 17).ToList();
        if (replies.Count != 1) return Reading.Empty("鼠标 A950", "鼠标", BatteryState.Unsupported, "没有唯一的匹配响应通道；未进行查询。");
        try
        {
            var (percent, frames) = await Transport.Query(replies[0], command, Protocols.DareuRequest(), true, r => Protocols.ParseDareu(r), ct);
            return new("鼠标 A950", "鼠标", percent.HasValue ? BatteryState.Valid : BatteryState.ReadFailed, percent,
                method, percent.HasValue ? "已验证接收器电量帧；充电标志尚未验证。" : "未收到可验证的电量帧；原因尚未确定，可能休眠、无线断连或通道被占用。请移动鼠标再刷新。",
                DateTimeOffset.Now, command.ReceiverKey, frames);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new("鼠标 A950", "鼠标", BatteryState.ReadFailed, null, method, ex.Message, DateTimeOffset.Now, command.ReceiverKey); }
    }
}

public sealed class XinmengS75Provider : IBatteryProvider
{
    public async Task<Reading> ReadAsync(List<HidDevice> devices, string? selectedReceiver, CancellationToken ct)
    {
        const string method = "S75 官方网页协议 · GET_DEVICE_INFO";
        var candidates = devices.Where(d => d.Vid == 0x0C45 && d.Pid == 0xFEFA && d.UsagePage == 0xFF60 &&
            d.Usage == 0x61 && d.InputLength == 65 && d.OutputLength == 65 && (selectedReceiver == null || d.ReceiverKey == selectedReceiver)).ToList();
        if (candidates.Count == 0) return Reading.Empty("键盘 S75", "键盘", devices.Any(d => d.Vid == 0x0C45 && d.Pid == 0xFEFA && (selectedReceiver == null || d.ReceiverKey == selectedReceiver)) ? BatteryState.Unsupported : BatteryState.Disconnected, "未找到选定的 0C45:FEFA / FF60 接口。其他硬件版本暂不支持；请查看 HID 列表。");
        if (candidates.Count > 1) return Reading.Empty("键盘 S75", "键盘", BatteryState.Unknown, "发现多个候选接收器，请在设置中选择目标。");
        var device = candidates[0];
        try
        {
            var (percent, frames) = await Transport.Query(device, device, Protocols.S75Request(), false, r => Protocols.ParseS75(r), ct);
            bool disconnected = frames.Split(" | ").Any(f => f.StartsWith("0055FC04", StringComparison.Ordinal));
            bool sleeping = frames.Split(" | ").Any(f => f.StartsWith("0055FC06", StringComparison.Ordinal));
            if (!percent.HasValue && (disconnected || sleeping))
                return new("键盘 S75", "键盘", disconnected ? BatteryState.WirelessDisconnected : BatteryState.Sleeping,
                    null, method, "接收器返回官方协议的无线断连 / 休眠状态通知；没有当前电量。请按键后刷新。", DateTimeOffset.Now, device.ReceiverKey, frames);
            return new("键盘 S75", "键盘", percent.HasValue ? BatteryState.Valid : BatteryState.ReadFailed, percent, method,
                percent.HasValue ? "已验证 S75 内部标识 0C45:8006 / 2:15 及电量字段。" : "未收到匹配 S75 身份和格式的响应；原因尚未确定。可能为其他共享接收器型号、休眠或无线断连。请按键后刷新。",
                DateTimeOffset.Now, device.ReceiverKey, frames);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new("键盘 S75", "键盘", BatteryState.ReadFailed, null, method, ex.Message, DateTimeOffset.Now, device.ReceiverKey); }
    }
}

public static class Probe
{
    public static async Task<Snapshot> RunAsync(AppSettings settings, CancellationToken ct)
    {
        var devices = HidDiscovery.Enumerate();
        // Sequential queries avoid competing with another collection/device's response channel.
        var mouse = await new DareuA950Provider().ReadAsync(devices, settings.MouseReceiver, ct);
        var keyboard = await new XinmengS75Provider().ReadAsync(devices, settings.KeyboardReceiver, ct);
        return new(DateTimeOffset.Now, devices, mouse, keyboard);
    }
}
