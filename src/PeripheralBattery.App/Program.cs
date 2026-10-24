using System.Text.Json;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--worker")
        {
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[2]), Json.Options) ?? new();
                var snapshot = Probe.RunAsync(settings, CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(snapshot, Json.Options)); return 0;
            }
            catch (Exception ex) { Storage.Log("查询进程: " + ex); return 1; }
        }
        if (args.Length == 2 && args[0] == "--diagnose")
        {
            try
            {
                var snapshot = WorkerClient.ReadAsync(Storage.Load(), CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(snapshot, Json.Options)); return 0;
            }
            catch (Exception ex) { Storage.Log("诊断: " + ex); return 1; }
        }
        using var mutex = new Mutex(true, @"Local\PeripheralBattery.Tray.v1", out bool first);
        if (!first)
        {
            int command = args.Contains("--quit") ? 3 : args.Contains("--diagnostics") ? 1 : args.Contains("--settings") ? 2 : 0;
            return CommandChannel.Send(command) ? 0 : 1;
        }
        if (args.Contains("--quit")) return 0;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => { Storage.Log("UI: " + e.Exception); MessageBox.Show("操作未完成：" + e.Exception.Message, "无线外设电量"); };
        try { using var context = new TrayContext(args.Contains("--show"), args.Contains("--diagnostics"), args.Contains("--settings")); Application.Run(context); return 0; }
        catch (Exception ex) { Storage.Log("启动失败: " + ex); MessageBox.Show(ex.Message, "无线外设电量启动失败"); return 1; }
    }
}
