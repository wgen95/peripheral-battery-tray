using System.Runtime.InteropServices;
using PeripheralBattery.Core;

namespace PeripheralBattery.App;

internal static class NativeUi
{
    [StructLayout(LayoutKind.Sequential)] internal struct RawInputDevice
    { public ushort Page, Usage; public uint Flags; public IntPtr Window; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

internal sealed class EventWindow : NativeWindow, IDisposable
{
    internal event Action? RefreshRequested;
    internal event Action? SuspendRequested;
    internal event Action? KeyboardResumed;
    internal KeyboardActivity Keyboard { get; } = new();
    internal EventWindow()
    {
        CreateHandle(new CreateParams { Caption = "PeripheralBattery.Events", ExStyle = 0x80, Style = unchecked((int)0x80000000) });
        // INPUTSINK only observes keyboard activity. No NOLEGACY, hooks or input suppression.
        if (!NativeUi.RegisterRawInputDevices([new() { Page = 1, Usage = 6, Flags = 0x100, Window = Handle }], 1,
            (uint)Marshal.SizeOf<NativeUi.RawInputDevice>())) Storage.Log("键盘活动通知不可用；自动键盘查询保持暂停。");
    }
    protected override void WndProc(ref Message m)
    {
        // Top-level hidden window receives broadcast device/power/display messages.
        if (m.Msg == 0x219 || m.Msg == 0x7E || m.Msg == 0x2E0) RefreshRequested?.Invoke();
        // Only the keyboard class was registered; do not read or retain RAWINPUT key data.
        if (m.Msg == 0xFF && Keyboard.Record(DateTimeOffset.Now)) KeyboardResumed?.Invoke();
        if (m.Msg == 0x218)
        {
            if (m.WParam.ToInt32() == 4) { Keyboard.Reset(); SuspendRequested?.Invoke(); }
            if (m.WParam.ToInt32() is 7 or 18) RefreshRequested?.Invoke();
        }
        base.WndProc(ref m);
    }
    public void Dispose()
    {
        NativeUi.RegisterRawInputDevices([new() { Page = 1, Usage = 6, Flags = 1 }], 1,
            (uint)Marshal.SizeOf<NativeUi.RawInputDevice>());
        DestroyHandle();
    }
}
