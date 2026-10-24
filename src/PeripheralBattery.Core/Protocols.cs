namespace PeripheralBattery.Core;

public static class Protocols
{
    public static byte[] DareuRequest()
    {
        var r = new byte[17]; r[0] = 8; r[1] = 4; r[16] = 0x49; return r;
    }
    public static int? ParseDareu(ReadOnlySpan<byte> r)
    {
        if (r.Length != 17 || r[0] != 9 || r[1] != 4 || r[2] != 0 || r[5] != 2 || r[6] > 100) return null;
        int sum = 0; for (int i = 0; i < 16; i++) sum += r[i];
        return r[16] == unchecked((byte)(0x55 - sum)) ? r[6] : null;
    }
    public static byte[] S75Request()
    {
        // Windows report ID 0 + official 64-byte AA frame, GET_DEVICE_INFO (16).
        var r = new byte[65]; r[1] = 0xAA; r[2] = 0x10; r[3] = 48; r[7] = 1; return r;
    }
    public static int? ParseS75(ReadOnlySpan<byte> r)
    {
        // Native Windows input includes the zero report ID. WebHID/hidapi omit it.
        if (r.Length != 65 || r[0] != 0 || r[1] != 0x55 || r[2] != 0x10 || r[3] != 48 || r[4] != 0 || r[5] != 0) return null;
        var d = r[9..];
        // Shared receiver VID/PID is insufficient: validate the internal S75 identity.
        if (d[4] != 0x45 || d[5] != 0x0C || d[6] != 6 || d[7] != 0x80 ||
            d[12] != 2 || d[13] != 0 || d[14] != 15 || d[15] != 0 || d[17] > 100) return null;
        return d[17];
    }
}
