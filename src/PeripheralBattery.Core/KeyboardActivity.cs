namespace PeripheralBattery.Core;

// Only a timestamp is retained. No key codes, text or device input are stored.
public sealed class KeyboardActivity
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(30);
    public DateTimeOffset? QueryUntil { get; private set; }
    public bool IsActive(DateTimeOffset now) => QueryUntil > now;
    public bool Record(DateTimeOffset now)
    {
        bool resumed = !IsActive(now);
        QueryUntil = now + ActiveWindow;
        return resumed;
    }
    public void Reset() => QueryUntil = null;
}
