using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal static class Storage
{
    internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralBattery");
    internal static string SettingsPath => Path.Combine(Root, "settings.json");
    internal static string LogPath => Path.Combine(Root, "app.log");
    internal static AppSettings Load()
    {
        Directory.CreateDirectory(Root);
        try
        {
            var s = File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json.Options) ?? new() : new();
            s.PollSeconds = Math.Clamp(s.PollSeconds, 15, 600); s.LowThreshold = Math.Clamp(s.LowThreshold, 1, 50);
            if (!Enum.IsDefined(s.ColorTheme)) s.ColorTheme = TrayColorTheme.Monochrome;
            return s;
        }
        catch (Exception e) { Log("设置读取失败，使用默认值: " + e.Message); return new(); }
    }
    internal static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Root);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json.Options)); File.Move(tmp, SettingsPath, true);
    }
    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024) File.Move(LogPath, LogPath + ".1", true);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { /* Logging must never crash the tray. */ }
    }
    internal static bool AutoStart
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("PeripheralBattery") is string; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            string app = Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "PeripheralBattery.Backend"
                ? Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "PeripheralBattery.exe") : Environment.ProcessPath!;
            if (value) key.SetValue("PeripheralBattery", $"\"{app}\"");
            else key.DeleteValue("PeripheralBattery", false);
        }
    }
    internal static void OpenLog() => Process.Start(new ProcessStartInfo("notepad.exe", LogPath) { UseShellExecute = true });
}

internal static class WorkerClient
{
    internal sealed record Request(AppSettings Settings, DateTimeOffset? KeyboardQueryUntil);
    internal static async Task<Snapshot> ReadAsync(AppSettings settings, CancellationToken ct, DateTimeOffset? keyboardQueryUntil = null)
    {
        Directory.CreateDirectory(Storage.Root);
        string output = Path.Combine(Storage.Root, "probe-" + Guid.NewGuid().ToString("N") + ".json");
        string input = output + ".settings";
        Process? worker = null;
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new Request(settings, keyboardQueryUntil), Json.Options), ct);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--worker"); start.ArgumentList.Add(output); start.ArgumentList.Add(input);
            worker = Process.Start(start) ?? throw new IOException("无法启动 HID 查询进程");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(8));
            try { await worker.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("HID 查询超过 8 秒，查询进程已结束。"); }
            if (worker.ExitCode != 0 || !File.Exists(output)) throw new IOException("HID 查询失败；请查看日志。");
            return JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(output, ct), Json.Options) ?? throw new IOException("诊断数据为空");
        }
        finally
        {
            if (worker != null)
            {
                try { if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(CancellationToken.None); } } catch (InvalidOperationException) { }
                worker.Dispose();
            }
            try { File.Delete(output); File.Delete(input); } catch (IOException) { }
        }
    }
}
