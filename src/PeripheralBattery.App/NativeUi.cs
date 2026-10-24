using System.Runtime.InteropServices;

namespace PeripheralBattery.App;

internal static class NativeUi
{
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

internal sealed class EventWindow : NativeWindow, IDisposable
{
    internal event Action? RefreshRequested;
    internal event Action? SuspendRequested;
    internal EventWindow() => CreateHandle(new CreateParams { Caption = "PeripheralBattery.Events", ExStyle = 0x80, Style = unchecked((int)0x80000000) });
    protected override void WndProc(ref Message m)
    {
        // Top-level hidden window receives broadcast device/power/display messages.
        if (m.Msg == 0x219 || m.Msg == 0x7E || m.Msg == 0x2E0) RefreshRequested?.Invoke();
        if (m.Msg == 0x218)
        {
            if (m.WParam.ToInt32() == 4) SuspendRequested?.Invoke();
            if (m.WParam.ToInt32() is 7 or 18) RefreshRequested?.Invoke();
        }
        base.WndProc(ref m);
    }
    public void Dispose() => DestroyHandle();
}
