using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBattery.Core;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData
    { public int Size; public Guid ClassGuid; public int Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo
    { public int Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes
    { public int Size; public ushort Vid, Pid, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, Input, Output, Feature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices,
            OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int needed, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out int needed);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_Device_IDW(uint dev, StringBuilder buffer, int size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes attributes);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle h, byte[] value, int bytes);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] value, int bytes);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, IntPtr values, ref ushort count, IntPtr data);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_SetFeature(SafeFileHandle h, byte[] report, int size);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FlushQueue(SafeFileHandle h);
    internal static SafeFileHandle Open(string path, bool async = false, uint access = 0xC0000000)
    {
        var h = CreateFileW(path, access, 3, IntPtr.Zero, 3, async ? 0x40000000u : 0, IntPtr.Zero);
        if (h.IsInvalid) { var error = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(error); }
        return h;
    }
}

public static class HidDiscovery
{
    // The size probe must fail with ERROR_INSUFFICIENT_BUFFER and leave enough
    // space for cbSize and a terminated UTF-16 path. Hot-unplug can fail earlier.
    internal static bool IsValidDetailBuffer(int bytes, int error) =>
        error == 122 && bytes >= (IntPtr.Size == 8 ? 8 : 6) && bytes <= 128 * 1024 && bytes % 2 == 0;

    public static List<HidDevice> Enumerate()
    {
        var result = new List<HidDevice>();
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var info = new Native.DeviceInfo { Size = Marshal.SizeOf<Native.DeviceInfo>() };
                bool unexpectedSuccess = Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int needed, ref info);
                if (unexpectedSuccess || !IsValidDetailBuffer(needed, Marshal.GetLastWin32Error())) continue;
                var detail = Marshal.AllocHGlobal(needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, needed, out _, ref info)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var id = new StringBuilder(512);
                    if (!Native.SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out _)) continue;
                    var parent = new StringBuilder(512);
                    if (Native.CM_Get_Parent(out var parentDev, info.DevInst, 0) == 0)
                        Native.CM_Get_Device_IDW(parentDev, parent, parent.Capacity, 0);
                    result.Add(Inspect(path, id.ToString(), parent.Length > 0 ? parent.ToString() : id.ToString()));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
    private static HidDevice Inspect(string path, string instance, string key)
    {
        var attr = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
        var caps = new Native.Caps(); var values = new List<ValueUsage>();
        string product = "未命名 HID", manufacturer = ""; string? error = null;
        try
        {
            using var h = Native.Open(path, access: 0);
            Native.HidD_GetAttributes(h, ref attr);
            var buf = new byte[256];
            if (Native.HidD_GetProductString(h, buf, buf.Length)) product = Encoding.Unicode.GetString(buf).TrimEnd('\0');
            Array.Clear(buf);
            if (Native.HidD_GetManufacturerString(h, buf, buf.Length)) manufacturer = Encoding.Unicode.GetString(buf).TrimEnd('\0');
            if (!Native.HidD_GetPreparsedData(h, out var prep)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (Native.HidP_GetCaps(prep, out caps) != 0x110000) throw new IOException("无法解析 HID 集合");
                foreach (var (type, count) in new[] { (0, caps.InputValues), (1, caps.OutputValues), (2, caps.FeatureValues) })
                {
                    if (count == 0) continue;
                    var memory = Marshal.AllocHGlobal(count * 72);
                    try
                    {
                        ushort length = count;
                        if (Native.HidP_GetValueCaps(type, memory, ref length, prep) != 0x110000) continue;
                        for (int i = 0; i < length; i++)
                        {
                            var p = memory + i * 72;
                            ushort usage = (ushort)Marshal.ReadInt16(p, 56);
                            values.Add(new(type, Marshal.ReadByte(p, 2), (ushort)Marshal.ReadInt16(p), usage,
                                Marshal.ReadByte(p, 12) != 0 ? (ushort)Marshal.ReadInt16(p, 58) : usage,
                                Marshal.ReadInt32(p, 40), Marshal.ReadInt32(p, 44)));
                        }
                    }
                    finally { Marshal.FreeHGlobal(memory); }
                }
            }
            finally { Native.HidD_FreePreparsedData(prep); }
        }
        catch (Exception ex) { error = ex.Message; }
        return new(path, instance, key, product, manufacturer, attr.Vid, attr.Pid, attr.Version,
            caps.UsagePage, caps.Usage, caps.Input, caps.Output, caps.Feature, values, error);
    }
}
