using System.Net;
using System.Text.RegularExpressions;

namespace RokidControl.Core.Connections;

public static partial class AdbParsers
{
    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var devices = new List<AdbDevice>();

        foreach (var rawLine in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith(
                    "List of devices",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = WhitespaceRegex().Split(rawLine);
            if (fields.Length >= 2)
            {
                devices.Add(new AdbDevice(fields[0], fields[1]));
            }
        }

        return devices;
    }

    public static IReadOnlyList<string> ParseMdnsAddresses(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = new List<string>();

        foreach (var rawLine in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            var fields = WhitespaceRegex().Split(rawLine);
            var serviceIndex = Array.FindIndex(
                fields,
                value => string.Equals(
                    value,
                    "_adb-tls-connect._tcp",
                    StringComparison.Ordinal));

            if (serviceIndex < 0 || serviceIndex + 1 >= fields.Length)
            {
                continue;
            }

            var address = fields[serviceIndex + 1].Trim();
            if (IsHostAndPort(address) &&
                !result.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(address);
            }
        }

        return result;
    }

    public static (int Width, int Height)? ParseScreenSize(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = ScreenSizeRegex().Match(output);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var width) ||
            !int.TryParse(match.Groups[2].Value, out var height) ||
            width <= 0 ||
            height <= 0)
        {
            return null;
        }

        return (width, height);
    }

    public static string? ParseIpv4Address(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = Ipv4Regex().Match(output);
        if (!match.Success ||
            !IPAddress.TryParse(match.Groups[1].Value, out var address) ||
            address.AddressFamily !=
            System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        return address.ToString();
    }

    public static bool IsRokidDevice(string model, string manufacturer)
    {
        var values = new[] { model, manufacturer };
        return values.Any(value =>
            value.Contains("rokid", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("rv101", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("rg-glasses", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHostAndPort(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator <= 0 ||
            separator == value.Length - 1 ||
            !ushort.TryParse(value[(separator + 1)..], out var port) ||
            port == 0)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(value[..separator]);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(\d+)\s*[x×]\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenSizeRegex();

    [GeneratedRegex(@"\binet\s+(\d+\.\d+\.\d+\.\d+)/")]
    private static partial Regex Ipv4Regex();
}

