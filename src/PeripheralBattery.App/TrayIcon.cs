using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PeripheralBattery.App;

// Shell v4 suppresses the native tooltip while our compact popup is enabled.
// A persistent GUID lets Explorer remember the user's notification-area placement.
internal sealed class TrayIcon : NativeWindow, IDisposable
{
    internal static readonly Guid Identity = new("6319a943-4e29-4c5e-a8da-a0e018535da5");
    private const int Callback = 0x8001;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private Icon icon;
    private bool visible, registered, disposed, nativeTip;
    private Point? pressPoint;
    private bool dragged;
    private string text = "Peripheral Battery";
    internal event Action<Point>? Selected;
    internal event Action<Point>? PointerMoved;
    internal event Action? PointerPressed;
    internal event Action? HoverClosed;
    internal ContextMenuStrip? ContextMenu { get; set; }
    internal bool ShowNativeTip
    {
        get => nativeTip;
        set { nativeTip = value; Modify(); }
    }
    internal string Text { get => text; set { text = value.Length > 127 ? value[..127] : value; Modify(); } }
    // Own the replacement only after Shell has accepted it; keep HWND/GUID intact.
    internal void SetIcon(Icon replacement)
    {
        var previous = icon;
        icon = replacement;
        if (registered)
        {
            var data = Data(); data.Flags = 2 | 0x20; // NIF_ICON | NIF_GUID only
            if (!Shell_NotifyIcon(1, ref data))
            {
                icon = previous; replacement.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "托盘图标更新失败");
            }
        }
        previous.Dispose();
    }
    internal bool Visible
    {
        get => visible;
        set
        {
            if (visible == value) return;
            visible = value;
            if (value) Add();
            else { var data = Data(); Shell_NotifyIcon(2, ref data); registered = false; }
        }
    }
    internal TrayIcon(Icon icon)
    {
        this.icon = icon;
        CreateHandle(new CreateParams { Caption = "PeripheralBattery.Tray", ExStyle = 0x80, Style = unchecked((int)0x80000000) });
        Visible = true;
    }
    private NotifyData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyData>(), Window = Handle, Id = 1,
        Flags = 1 | 2 | 4 | 0x20 | (nativeTip ? 0x80u : 0), Message = Callback,
        Icon = icon.Handle, Tip = text, Info = "", InfoTitle = "", Guid = Identity
    };
    private void Add()
    {
        var data = Data();
        registered = Shell_NotifyIcon(0, ref data);
        if (!registered) { Storage.Log("托盘图标注册失败: " + Marshal.GetLastWin32Error()); return; }
        data.Version = 4;
        if (!Shell_NotifyIcon(4, ref data)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用 Shell v4 托盘交互");
    }
    private void Modify() { if (!registered) return; var data = Data(); Shell_NotifyIcon(1, ref data); }
    internal Rectangle? Bounds
    {
        get
        {
            var id = new Identifier { Size = (uint)Marshal.SizeOf<Identifier>(), Window = Handle, Id = 1, Guid = Identity };
            return Shell_NotifyIconGetRect(ref id, out var rect) == 0
                ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if ((uint)m.Msg == taskbarCreated && visible) { registered = false; Add(); }
        if (m.Msg == Callback)
        {
            int code = (int)(m.LParam.ToInt64() & 0xFFFF);
            var point = new Point(unchecked((short)(m.WParam.ToInt64() & 0xFFFF)), unchecked((short)((m.WParam.ToInt64() >> 16) & 0xFFFF)));
            Dispatch(code, point);
        }
        base.WndProc(ref m);
    }
    internal void Dispatch(int code, Point point)
    {
        if (code == 0x201) { pressPoint = point; dragged = false; PointerPressed?.Invoke(); }
        if (code == 0x200)
        {
            if (pressPoint is Point start && (Math.Abs(point.X - start.X) > SystemInformation.DragSize.Width / 2 ||
                Math.Abs(point.Y - start.Y) > SystemInformation.DragSize.Height / 2)) dragged = true;
            PointerMoved?.Invoke(point);
        }
        // NIN_SELECT / NIN_KEYSELECT. Do not turn a drag into a popup click.
        if (code is 0x400 or 0x401)
        {
            bool moved = pressPoint is Point start && (Math.Abs(point.X - start.X) > SystemInformation.DragSize.Width / 2 ||
                Math.Abs(point.Y - start.Y) > SystemInformation.DragSize.Height / 2);
            if (code == 0x401 || (!dragged && !moved)) Selected?.Invoke(point);
            pressPoint = null; dragged = false;
        }
        if (code == 0x406) PointerMoved?.Invoke(point); // NIN_POPUPOPEN
        if (code == 0x407) HoverClosed?.Invoke(); // NIN_POPUPCLOSE
        if (code == 0x7B && ContextMenu != null)
        {
            pressPoint = null; PointerPressed?.Invoke();
            SetForegroundWindow(Handle);
            ContextMenu.Show(point.X == -1 ? Cursor.Position : point);
            PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        Visible = false; disposed = true; DestroyHandle(); icon.Dispose();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Message; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Identifier
    { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
