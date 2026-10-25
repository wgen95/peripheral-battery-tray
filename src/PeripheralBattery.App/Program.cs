using System.Text.Json;
using System.Runtime.CompilerServices;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--worker") return RunWorker(args);
        if (args.Length == 2 && args[0] == "--diagnose") return RunDiagnostic(args[1]);
        if (args.Length == 1 && args[0] is "--settings-window" or "--diagnostics-window") return RunWindow(args[0]);
        return 0;
    }
    // Keep WinForms types out of the worker entry point's JIT / assembly loading.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWorker(string[] args)
    {
            try
            {
                var request = JsonSerializer.Deserialize<WorkerClient.Request>(File.ReadAllText(args[2]), Json.Options) ?? throw new IOException("查询请求为空");
                var snapshot = Probe.RunAsync(request.Settings, CancellationToken.None, request.KeyboardQueryUntil).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(snapshot, Json.Options)); return 0;
            }
            catch (Exception ex) { Storage.Log("查询进程: " + ex); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunDiagnostic(string output)
    {
            try
            {
                var snapshot = WorkerClient.ReadAsync(Storage.Load(), CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(Path.GetFullPath(output), JsonSerializer.Serialize(snapshot, Json.Options)); return 0;
            }
            catch (Exception ex) { Storage.Log("诊断: " + ex); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWindow(string command)
    {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (command == "--settings-window")
            {
                var snapshot = ReadLiveSnapshot();
                using var form = new SettingsForm(Storage.Load(), snapshot?.Devices ?? [], _ => CommandChannel.Send(5));
                Application.Run(form);
            }
            else
            {
                using var form = new DiagnosticsForm(() => CommandChannel.Send(4));
                DateTime snapshotTime = DateTime.MinValue;
                void RefreshView()
                {
                    string path = Path.Combine(Storage.Root, "live-snapshot.json");
                    var time = File.GetLastWriteTimeUtc(path); if (time == snapshotTime) return;
                    var snapshot = ReadLiveSnapshot(); if (snapshot == null) return;
                    Dictionary<string, Reading> history = new();
                    try { history = JsonSerializer.Deserialize<Dictionary<string, Reading>>(File.ReadAllText(Path.Combine(Storage.Root, "live-history.json")), Json.Options) ?? new(); } catch (Exception e) when (e is IOException or JsonException) { }
                    form.UpdateSnapshot(snapshot, history); snapshotTime = time;
                }
                RefreshView(); using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                timer.Tick += (_, _) => RefreshView(); timer.Start(); Application.Run(form);
            }
            return 0;
    }
    private static Snapshot? ReadLiveSnapshot()
    {
        try { return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(Storage.Root, "live-snapshot.json")), Json.Options); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
}
