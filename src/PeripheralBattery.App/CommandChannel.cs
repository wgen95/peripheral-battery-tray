namespace PeripheralBattery.App;

internal sealed class CommandChannel : IDisposable
{
    private readonly List<(EventWaitHandle Event, RegisteredWaitHandle Wait)> channels = [];
    internal CommandChannel(Control target, Action<int> received)
    {
        _ = target.Handle;
        for (int i = 0; i < 4; i++)
        {
            int command = i;
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name(i));
            var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
            {
                try { if (!target.IsDisposed) target.BeginInvoke(() => received(command)); }
                catch (InvalidOperationException) { /* UI is shutting down. */ }
            }, null, Timeout.Infinite, false);
            channels.Add((signal, wait));
        }
    }
    private static string Name(int command) => @"Local\PeripheralBattery.Command.v1." + command;
    internal static bool Send(int command)
    {
        if (!EventWaitHandle.TryOpenExisting(Name(command), out var signal)) return false;
        using (signal) return signal.Set();
    }
    public void Dispose()
    { foreach (var (signal, wait) in channels) { wait.Unregister(null); signal.Dispose(); } }
}
