using PeripheralBattery.Core;
using System.Text.Json;

if (args.Contains("--hardware"))
{
    var snapshot = await Probe.RunAsync(new(), CancellationToken.None);
    var json = JsonSerializer.Serialize(snapshot, Json.Options);
    File.WriteAllText(args.Last(), json);
    Console.WriteLine($"HID collections: {snapshot.Devices.Count}; mouse: {snapshot.Mouse.Display}; keyboard: {snapshot.Keyboard.Display}");
    Console.WriteLine(snapshot.Mouse.Detail); Console.WriteLine(snapshot.Keyboard.Detail);
    return;
}
int count = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
byte[] Dareu(byte level)
{
    var r = new byte[17]; r[0] = 9; r[1] = 4; r[5] = 2; r[6] = level;
    r[16] = unchecked((byte)(0x55 - r.Take(16).Sum(v => v))); return r;
}
var real = Convert.FromHexString("0904000000023201000000000000000013");
Check(Protocols.ParseDareu(real) == 50, "captured A950 frame");
foreach (byte level in new byte[] { 0, 1, 50, 100 }) Check(Protocols.ParseDareu(Dareu(level)) == level, "valid range");
foreach (byte level in new byte[] { 101, 255 }) Check(Protocols.ParseDareu(Dareu(level)) == null, "out of range");
foreach (int index in new[] { 0, 1, 2, 5, 16 })
{
    var bad = (byte[])real.Clone(); bad[index] ^= 1;
    // Recalculate checksum so header/status checks are independently exercised.
    if (index != 16) bad[16] = unchecked((byte)(0x55 - bad.Take(16).Sum(v => v)));
    Check(Protocols.ParseDareu(bad) == null, $"reject invalid field {index}");
}
Check(Protocols.ParseDareu(real.AsSpan(0, 8)) == null, "short frame");
Check(Protocols.ParseDareu(new byte[18]) == null, "long frame");
Check(Protocols.DareuRequest().SequenceEqual(Convert.FromHexString("0804000000000000000000000000000049")), "request checksum");
var keyboard = new byte[65]; keyboard[1] = 0x55; keyboard[2] = 0x10; keyboard[3] = 48;
keyboard[13] = 0x45; keyboard[14] = 0x0c; keyboard[15] = 6; keyboard[16] = 0x80;
keyboard[21] = 2; keyboard[23] = 15; keyboard[26] = 57;
Check(Protocols.ParseS75(keyboard) == 57, "S75 official offsets");
foreach (int index in new[] { 0, 1, 2, 3, 4, 5, 13, 14, 15, 16, 21, 22, 23, 24 })
{
    var bad = (byte[])keyboard.Clone(); bad[index] ^= 1;
    Check(Protocols.ParseS75(bad) == null, $"reject S75 identity/header {index}");
}
keyboard[26] = 101; Check(Protocols.ParseS75(keyboard) == null, "S75 invalid percentage");
Check(Protocols.ParseS75(new byte[20]) == null, "S75 truncated");
foreach (var state in Enum.GetValues<BatteryState>().Where(s => s != BatteryState.Valid))
    Check(!new Reading("test", "test", state, 77, "", "", DateTimeOffset.Now).Display.Contains('%'), "never expose stale percent");
Check(Reading.Empty("a", "b", BatteryState.ReadFailed, "error").Percent == null, "failed result has no value");
Check(Protocols.S75Request().Length == 65 && Protocols.S75Request()[7] == 1, "S75 report ID and packet flag");
Console.WriteLine($"PASS: {count} protocol and state assertions (synthetic fixtures; hardware is separate).");
