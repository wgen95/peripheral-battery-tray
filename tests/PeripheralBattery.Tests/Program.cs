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
var activity = new KeyboardActivity(); var now = DateTimeOffset.Now;
Check(!activity.IsActive(now), "startup must not wake a keyboard");
Check(activity.Record(now) && activity.IsActive(now.AddSeconds(29)), "physical keyboard activity opens a bounded query window");
Check(!activity.IsActive(now.AddSeconds(30)), "idle timeout stops queries before two-minute sleep");
Check(!activity.Record(now.AddSeconds(10)), "continuous typing does not repeatedly request immediate probes");
Check(activity.IsActive(now.AddSeconds(39)) && !activity.IsActive(now.AddSeconds(40)), "typing extends only activity window");
activity.Reset(); Check(!activity.IsActive(now), "suspend clears previous activity");
var provider = new CountingProvider();
var compatible = new HidDevice("fixture-a", "fixture-id", "receiver-a", "S75", "fixture", 0x0C45, 0xFEFA, 0,
    0xFF60, 0x61, 65, 65, 0, [], null);
var idle = await Probe.ReadKeyboardAsync([compatible], null, null, CancellationToken.None, provider);
Check(provider.Calls == 0 && idle.State == BatteryState.Idle && idle.Percent == null, "idle request must not call HID provider or reuse percentage");
await Probe.ReadKeyboardAsync([compatible], null, now.AddSeconds(-1), CancellationToken.None, provider);
Check(provider.Calls == 0, "expired worker request must not touch keyboard");
var unplugged = await Probe.ReadKeyboardAsync([], null, null, CancellationToken.None, provider);
Check(unplugged.State == BatteryState.Disconnected && unplugged.Percent == null, "an absent idle receiver is disconnected, not waiting for use");
var incompatible = await Probe.ReadKeyboardAsync([compatible with { OutputLength = 0 }], null, null, CancellationToken.None, provider);
Check(incompatible.State == BatteryState.Unsupported, "an idle receiver with incompatible report capabilities is not called supported");
var other = compatible with { Path = "fixture-b", ReceiverKey = "receiver-b" };
var ambiguous = await Probe.ReadKeyboardAsync([compatible, other], null, null, CancellationToken.None, provider);
Check(ambiguous.State == BatteryState.Unknown, "multiple idle receiver candidates are not guessed");
var missingSelection = await Probe.ReadKeyboardAsync([other], compatible.ReceiverKey, null, CancellationToken.None, provider);
Check(missingSelection.State == BatteryState.Disconnected, "saved receiver selection never silently falls back to another receiver");
var selected = await Probe.ReadKeyboardAsync([compatible, other], compatible.ReceiverKey, null, CancellationToken.None, provider);
Check(selected.State == BatteryState.Idle && provider.Calls == 0, "a unique selected idle receiver is resolved without any provider calls");
await Probe.ReadKeyboardAsync([], null, DateTimeOffset.Now.AddSeconds(10), CancellationToken.None, provider);
Check(provider.Calls == 1, "an active keyboard may be queried");
Check(HidDiscovery.IsValidDetailBuffer(512, 122), "valid UTF-16 interface detail allocation accepted");
foreach (var (bytes, error) in new[] { (0, 122), (-1, 122), (4, 122), (9, 122), (131074, 122), (512, 0), (512, 1167) })
    Check(!HidDiscovery.IsValidDetailBuffer(bytes, error), "invalid or disconnected interface size probe never reaches allocation");
Console.WriteLine($"PASS: {count} protocol and state assertions (synthetic fixtures; hardware is separate).");

sealed class CountingProvider : IBatteryProvider
{
    public int Calls;
    public Task<Reading> ReadAsync(List<HidDevice> devices, string? receiver, CancellationToken ct)
    { Calls++; return Task.FromResult(Reading.Empty("S75", "键盘", BatteryState.Unknown, "test")); }
}
