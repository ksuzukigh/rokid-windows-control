namespace RokidControl.Core.Connections;

public sealed record AdbDevice(string Serial, string State)
{
    public bool IsReady => string.Equals(
        State,
        "device",
        StringComparison.OrdinalIgnoreCase);

    public bool IsUsb => !Serial.Contains(':', StringComparison.Ordinal);

    public bool IsNetwork => !IsUsb;
}

