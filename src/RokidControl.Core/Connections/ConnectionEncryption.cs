namespace RokidControl.Core.Connections;

public enum ConnectionEncryptionVerdict
{
    Encrypted,
    PlaintextConnection,
    PlaintextListenerRemains,
    WirelessDebuggingDisabled,
    NotNetworkAddress,
}

public sealed record ConnectionEncryptionResult(
    ConnectionEncryptionVerdict Verdict,
    IReadOnlyList<string> Ports)
{
    public bool IsEncrypted => Verdict == ConnectionEncryptionVerdict.Encrypted;

    public bool IsPlaintextListenerProblem =>
        Verdict is ConnectionEncryptionVerdict.PlaintextConnection or
            ConnectionEncryptionVerdict.PlaintextListenerRemains;
}

public static class ConnectionEncryption
{
    public static string? GetPort(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var separator = address.LastIndexOf(':');
        if (separator < 0 || separator == address.Length - 1)
        {
            return null;
        }

        var port = address[(separator + 1)..];
        return port.All(char.IsAsciiDigit) ? port : null;
    }

    public static bool IsActiveListenerPort(string value) =>
        int.TryParse(value.Trim(), out var port) && port is > 0 and <= 65535;

    public static IReadOnlyList<string> ActiveListenerPorts(
        IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var unique = new List<string>();
        foreach (var value in values)
        {
            var port = value.Trim();
            if (IsActiveListenerPort(port) &&
                !unique.Contains(port, StringComparer.Ordinal))
            {
                unique.Add(port);
            }
        }

        return unique;
    }

    public static ConnectionEncryptionResult Inspect(
        string address,
        IEnumerable<string> plaintextPorts,
        bool wirelessDebuggingEnabled)
    {
        var connectionPort = GetPort(address);
        if (connectionPort is null)
        {
            return new(
                ConnectionEncryptionVerdict.NotNetworkAddress,
                Array.Empty<string>());
        }

        var listening = ActiveListenerPorts(plaintextPorts);
        if (listening.Contains(connectionPort, StringComparer.Ordinal))
        {
            return new(
                ConnectionEncryptionVerdict.PlaintextConnection,
                listening);
        }

        if (listening.Count > 0)
        {
            return new(
                ConnectionEncryptionVerdict.PlaintextListenerRemains,
                listening);
        }

        return new(
            wirelessDebuggingEnabled
                ? ConnectionEncryptionVerdict.Encrypted
                : ConnectionEncryptionVerdict.WirelessDebuggingDisabled,
            Array.Empty<string>());
    }

    public static string? RejectionReason(ConnectionEncryptionResult result) =>
        result.Verdict switch
        {
            ConnectionEncryptionVerdict.Encrypted => null,
            ConnectionEncryptionVerdict.PlaintextConnection =>
                $"接続先が暗号化されていないADB入口です（ポート{string.Join("・", result.Ports)}）",
            ConnectionEncryptionVerdict.PlaintextListenerRemains =>
                $"端末に暗号化されていないADB入口が残っています（ポート{string.Join("・", result.Ports)}）",
            ConnectionEncryptionVerdict.WirelessDebuggingDisabled =>
                "Androidの暗号化ワイヤレスデバッグが有効ではありません",
            ConnectionEncryptionVerdict.NotNetworkAddress =>
                "ネットワーク接続先の形式ではありません",
            _ => "接続の暗号化を確認できません",
        };

    public static string? BuildUsbTlsAddress(
        string ipAddressOutput,
        string tlsPortOutput)
    {
        var address = AdbParsers.ParseIpv4Address(ipAddressOutput);
        var ports = ActiveListenerPorts(
            tlsPortOutput.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries));
        return address is not null &&
            IsUsableIpv4Address(address) &&
            ports.Count == 1
            ? $"{address}:{ports[0]}"
            : null;
    }

    private static bool IsUsableIpv4Address(string address)
    {
        var parts = address.Split('.');
        if (parts.Length != 4 ||
            parts.Any(part =>
                !byte.TryParse(part, out _)))
        {
            return false;
        }

        _ = byte.TryParse(parts[0], out var first);
        return first is > 0 and < 224 && first != 127;
    }
}
