using System.Globalization;

namespace RokidControl.Core.Connections;

public sealed class RokidConnectionManager : IAsyncDisposable
{
    private const string RemoteWatchdog =
        "/data/local/tmp/rokid_windows_wifi_watchdog.sh";
    private const string RemoteHeartbeat =
        "/data/local/tmp/rokid_windows_control_heartbeat";
    private const string RemoteWatchdogPid =
        "/data/local/tmp/rokid_windows_wifi_watchdog.pid";
    private static readonly TimeSpan SecureWifiHandoffDuration =
        TimeSpan.FromSeconds(15);
    private static readonly string[] PlaintextPortProperties =
    [
        "service.adb.tcp.port",
        "persist.adb.tcp.port",
    ];

    private readonly IAdbClient _adb;
    private readonly string _addressFile;
    private readonly string _watchdogFile;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private PeriodicTimer? _heartbeatTimer;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;
    private string _serial = string.Empty;
    private bool _sawPlaintextListener;

    public RokidConnectionManager(
        IAdbClient adb,
        string addressFile,
        string watchdogFile)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        ArgumentException.ThrowIfNullOrWhiteSpace(addressFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(watchdogFile);
        _addressFile = addressFile;
        _watchdogFile = watchdogFile;
    }

    public async Task<string> GetCurrentSerialAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return _serial;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task PrepareAdbServerAsync(
        CancellationToken cancellationToken = default)
    {
        _ = await _adb.RunAsync(
            ["start-server"],
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ConnectForStartupAsync(
        IProgress<string>? progress = null,
        TimeSpan? searchDuration = null,
        CancellationToken cancellationToken = default)
    {
        _sawPlaintextListener = false;
        progress?.Report("Rokidを探しています…");

        var usb = await FindUsbRokidAsync(cancellationToken).ConfigureAwait(false);
        if (usb is not null)
        {
            return await ConnectUsingUsbBootstrapAsync(
                usb,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        var saved = await ReadSavedAddressAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null && IsLegacyFixedPortAddress(saved))
        {
            await RejectWifiDeviceAsync(
                saved,
                removeSavedAddress: true,
                cancellationToken).ConfigureAwait(false);
            saved = null;
        }

        if (saved is not null && await ConnectAsync(saved, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report("Rokidに接続しています…");
            if (await IsRokidDeviceAsync(saved, cancellationToken).ConfigureAwait(false) &&
                await IsSecureNetworkConnectionAsync(saved, cancellationToken)
                    .ConfigureAwait(false))
            {
                return await UseSerialAsync(saved, saveAddress: true).ConfigureAwait(false);
            }

            await RejectWifiDeviceAsync(
                saved,
                removeSavedAddress: true,
                cancellationToken).ConfigureAwait(false);
        }

        var connectedWifi = await FindConnectedWifiRokidAsync(cancellationToken)
            .ConfigureAwait(false);
        if (connectedWifi is not null)
        {
            progress?.Report("Rokidに接続しています…");
            return await UseSerialAsync(connectedWifi, saveAddress: true)
                .ConfigureAwait(false);
        }

        var discovered = await ConnectToDiscoveredRokidAsync(
            progress,
            cancellationToken).ConfigureAwait(false);
        if (discovered is not null)
        {
            return discovered;
        }

        var deadline = DateTimeOffset.UtcNow +
            (searchDuration ?? TimeSpan.FromSeconds(60));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Rokidを探しています…");

            discovered = await ConnectToDiscoveredRokidAsync(
                progress,
                cancellationToken).ConfigureAwait(false);
            if (discovered is not null)
            {
                return discovered;
            }

            usb = await FindUsbRokidAsync(cancellationToken).ConfigureAwait(false);
            if (usb is not null)
            {
                return await ConnectUsingUsbBootstrapAsync(
                    usb,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RokidConnectionException(
            _sawPlaintextListener
                ? RokidConnectionError.PlaintextListenerRemains
                : RokidConnectionError.NoDevice);
    }

    public async Task<string?> ReconnectAsync(
        CancellationToken cancellationToken = default)
    {
        _sawPlaintextListener = false;
        var previous = await GetCurrentSerialAsync().ConfigureAwait(false);
        var saved = await ReadSavedAddressAsync(cancellationToken)
            .ConfigureAwait(false);
        if (saved is not null && IsLegacyFixedPortAddress(saved))
        {
            await RejectWifiDeviceAsync(
                saved,
                removeSavedAddress: true,
                cancellationToken).ConfigureAwait(false);
            saved = null;
        }

        if (previous.Contains(':', StringComparison.Ordinal))
        {
            _ = await _adb.RunAsync(
                ["disconnect", previous],
                TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false);
            if (IsLegacyFixedPortAddress(previous))
            {
                previous = string.Empty;
            }
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var usb = await FindUsbRokidAsync(cancellationToken).ConfigureAwait(false);
            if (usb is not null)
            {
                return await ConnectUsingUsbBootstrapAsync(
                    usb,
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
            }

            var wifiCandidates = new[]
                {
                    previous.Contains(':', StringComparison.Ordinal)
                        ? previous
                        : null,
                    saved,
                }
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Where(candidate => !IsLegacyFixedPortAddress(candidate!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>();
            foreach (var candidate in wifiCandidates)
            {
                if (!await ConnectAsync(candidate, cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                if (await IsRokidDeviceAsync(candidate, cancellationToken)
                        .ConfigureAwait(false) &&
                    await IsSecureNetworkConnectionAsync(
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return await UseSerialAsync(candidate, saveAddress: true)
                        .ConfigureAwait(false);
                }

                var isSavedAddress = string.Equals(
                    candidate,
                    saved,
                    StringComparison.OrdinalIgnoreCase);
                await RejectWifiDeviceAsync(
                    candidate,
                    removeSavedAddress: isSavedAddress,
                    cancellationToken).ConfigureAwait(false);
                if (string.Equals(
                        candidate,
                        previous,
                        StringComparison.OrdinalIgnoreCase))
                {
                    previous = string.Empty;
                }

                if (isSavedAddress)
                {
                    saved = null;
                }
            }

            var connectedWifi = await FindConnectedWifiRokidAsync(cancellationToken)
                .ConfigureAwait(false);
            if (connectedWifi is not null)
            {
                return await UseSerialAsync(connectedWifi, saveAddress: true)
                    .ConfigureAwait(false);
            }

            var discovered = await ConnectToDiscoveredRokidAsync(
                progress: null,
                cancellationToken).ConfigureAwait(false);
            if (discovered is not null)
            {
                return discovered;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        if (_sawPlaintextListener)
        {
            throw new RokidConnectionException(
                RokidConnectionError.PlaintextListenerRemains);
        }

        return null;
    }

    public async Task<(int Width, int Height)> GetScreenSizeAsync(
        CancellationToken cancellationToken = default)
    {
        var serial = await GetCurrentSerialAsync().ConfigureAwait(false);
        var result = await _adb.RunAsync(
            ["-s", serial, "shell", "wm", "size"],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        return AdbParsers.ParseScreenSize(result.CombinedOutput) ?? (480, 640);
    }

    public async Task<bool> IsCurrentConnectionAliveAsync(
        CancellationToken cancellationToken = default)
    {
        var serial = await GetCurrentSerialAsync().ConfigureAwait(false);
        return !string.IsNullOrEmpty(serial) &&
            await IsConnectedAsync(serial, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendKeyEventAsync(
        string androidKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(androidKey);
        var serial = await RequireCurrentSerialAsync().ConfigureAwait(false);
        _ = await _adb.RunAsync(
            ["-s", serial, "shell", "input", "keyevent", androidKey],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task TapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        var serial = await RequireCurrentSerialAsync().ConfigureAwait(false);
        _ = await _adb.RunAsync(
            [
                "-s", serial, "shell", "input", "tap",
                x.ToString(CultureInfo.InvariantCulture),
                y.ToString(CultureInfo.InvariantCulture),
            ],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool?> IsOriginalCameraForegroundAsync(
        CancellationToken cancellationToken = default)
    {
        var serial = await RequireCurrentSerialAsync().ConfigureAwait(false);
        var result = await _adb.RunAsync(
            [
                "-s", serial, "shell", "dumpsys", "activity", "activities",
            ],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? CameraAppPolicy.IsOriginalCameraForeground(result.Output)
            : null;
    }

    public async Task<bool> IsLauncherActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var serial = await RequireCurrentSerialAsync().ConfigureAwait(false);
        var result = await _adb.RunAsync(
            [
                "-s", serial, "shell",
                "dumpsys activity activities | grep 'ResumedActivity:'",
            ],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        return result.Output.Contains(
            "com.rokid.os.sprite.launcher/",
            StringComparison.Ordinal);
    }

    public async Task StartWindowsModeAsync(
        CancellationToken cancellationToken = default)
    {
        await StopHeartbeatAsync().ConfigureAwait(false);
        var serial = await GetCurrentSerialAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(serial) || !File.Exists(_watchdogFile))
        {
            throw new RokidConnectionException(
                File.Exists(_watchdogFile)
                    ? RokidConnectionError.WatchdogFailed
                    : RokidConnectionError.MissingResource,
                Path.GetFileName(_watchdogFile));
        }

        await RequireSuccessAsync(
            ["-s", serial, "push", _watchdogFile, RemoteWatchdog],
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(
            ["-s", serial, "shell", "chmod", "700", RemoteWatchdog],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        var oldPid = await _adb.RunAsync(
            ["-s", serial, "shell", "cat", RemoteWatchdogPid],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (int.TryParse(
                oldPid.Output.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedPid))
        {
            _ = await _adb.RunAsync(
                ["-s", serial, "shell", "kill", parsedPid.ToString(CultureInfo.InvariantCulture)],
                TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false);
        }

        _ = await _adb.RunAsync(
            ["-s", serial, "shell", "rm", "-f", RemoteWatchdogPid],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(
            ["-s", serial, "shell", "touch", RemoteHeartbeat],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);

        var launch = $"setsid sh '{RemoteWatchdog}' 20 </dev/null >/dev/null 2>&1 &";
        await RequireSuccessAsync(
            ["-s", serial, "shell", launch],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
            .ConfigureAwait(false);

        var newPid = await _adb.RunAsync(
            ["-s", serial, "shell", "cat", RemoteWatchdogPid],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(
                newPid.Output.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _))
        {
            throw new RokidConnectionException(RokidConnectionError.WatchdogFailed);
        }

        _heartbeatCancellation = new CancellationTokenSource();
        _heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _heartbeatTask = RunHeartbeatAsync(
            _heartbeatTimer,
            _heartbeatCancellation.Token);
    }

    public async Task StopWindowsModeAsync(
        CancellationToken cancellationToken = default)
    {
        await StopHeartbeatAsync().ConfigureAwait(false);
        var serial = await GetCurrentSerialAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(serial))
        {
            return;
        }

        var pid = await _adb.RunAsync(
            ["-s", serial, "shell", "cat", RemoteWatchdogPid],
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
        if (int.TryParse(
                pid.Output.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedPid))
        {
            _ = await _adb.RunAsync(
                ["-s", serial, "shell", "kill", parsedPid.ToString(CultureInfo.InvariantCulture)],
                TimeSpan.FromSeconds(2),
                cancellationToken).ConfigureAwait(false);
        }

        _ = await _adb.RunAsync(
            [
                "-s", serial, "shell", "rm", "-f",
                RemoteHeartbeat, RemoteWatchdogPid,
            ],
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopHeartbeatAsync().ConfigureAwait(false);
        _stateLock.Dispose();
    }

    private async Task<string?> FindUsbRokidAsync(
        CancellationToken cancellationToken)
    {
        var result = await _adb.RunAsync(
            ["devices"],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);

        foreach (var device in AdbParsers.ParseDevices(result.Output)
                     .Where(device => device.IsReady && device.IsUsb))
        {
            if (await IsRokidDeviceAsync(device.Serial, cancellationToken)
                    .ConfigureAwait(false))
            {
                return device.Serial;
            }
        }

        return null;
    }

    private async Task<string?> FindConnectedWifiRokidAsync(
        CancellationToken cancellationToken)
    {
        var result = await _adb.RunAsync(
            ["devices"],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);

        foreach (var device in AdbParsers.ParseDevices(result.Output)
                     .Where(device => device.IsReady && device.IsNetwork))
        {
            if (IsLegacyFixedPortAddress(device.Serial))
            {
                _ = await _adb.RunAsync(
                    ["disconnect", device.Serial],
                    TimeSpan.FromSeconds(3),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (await IsRokidDeviceAsync(device.Serial, cancellationToken)
                    .ConfigureAwait(false) &&
                await IsSecureNetworkConnectionAsync(
                        device.Serial,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return device.Serial;
            }
        }

        return null;
    }

    private async Task<string> ConnectUsingUsbBootstrapAsync(
        string usbSerial,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("RokidにUSB接続しています…");
        _ = await UseSerialAsync(usbSerial, saveAddress: false)
            .ConfigureAwait(false);

        progress?.Report("無線接続を準備しています…");
        if (!await EnableSecureWifiFromUsbAsync(
                usbSerial,
                cancellationToken).ConfigureAwait(false))
        {
            return usbSerial;
        }

        var deadline = DateTimeOffset.UtcNow + SecureWifiHandoffDuration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var usbTlsAddress = await GetSecureWifiAddressFromUsbAsync(
                usbSerial,
                cancellationToken).ConfigureAwait(false);
            if (usbTlsAddress is not null &&
                await ConnectAsync(usbTlsAddress, cancellationToken)
                    .ConfigureAwait(false))
            {
                if (await IsRokidDeviceAsync(usbTlsAddress, cancellationToken)
                        .ConfigureAwait(false) &&
                    await IsSecureNetworkConnectionAsync(
                            usbTlsAddress,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    progress?.Report("無線接続に切り替えています…");
                    return await UseSerialAsync(
                        usbTlsAddress,
                        saveAddress: true).ConfigureAwait(false);
                }

                await RejectWifiDeviceAsync(
                    usbTlsAddress,
                    removeSavedAddress: false,
                    cancellationToken).ConfigureAwait(false);
            }

            var connectedWifi = await FindConnectedWifiRokidAsync(cancellationToken)
                .ConfigureAwait(false);
            if (connectedWifi is not null)
            {
                progress?.Report("無線接続に切り替えています…");
                return await UseSerialAsync(connectedWifi, saveAddress: true)
                    .ConfigureAwait(false);
            }

            var discovered = await ConnectToDiscoveredRokidAsync(
                progress,
                cancellationToken).ConfigureAwait(false);
            if (discovered is not null)
            {
                return discovered;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report("USB接続を使用します…");
        return usbSerial;
    }

    private async Task<bool> EnableSecureWifiFromUsbAsync(
        string usbSerial,
        CancellationToken cancellationToken)
    {
        if (!await ClosePlaintextListenersAsync(
                usbSerial,
                cancellationToken).ConfigureAwait(false))
        {
            throw new RokidConnectionException(
                RokidConnectionError.SafetyUnverified);
        }

        _ = await _adb.RunAsync(
            [
                "-s", usbSerial, "shell", "am", "broadcast",
                "-a", "com.rokid.os.master.assist.server.cmd",
                "-p", "com.rokid.os.sprite.assistserver",
                "--es", "cmd_type", "setting_change",
                "--es", "value",
                "[{\"key\":\"settings_wifi_enable\",\"value\":\"true\"}]",
            ],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        _ = await _adb.RunAsync(
            [
                "-s", usbSerial, "shell", "cmd", "wifi",
                "set-wifi-enabled", "enabled",
            ],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        var enable = await _adb.RunAsync(
            [
                "-s", usbSerial, "shell", "settings", "put", "global",
                "adb_wifi_enabled", "1",
            ],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        if (!enable.Succeeded)
        {
            return false;
        }

        var verify = await _adb.RunAsync(
            [
                "-s", usbSerial, "shell", "settings", "get", "global",
                "adb_wifi_enabled",
            ],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        return verify.Succeeded &&
            string.Equals(
                verify.Output.Trim(),
                "1",
                StringComparison.Ordinal);
    }

    private async Task<string?> ConnectToDiscoveredRokidAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = await _adb.RunAsync(
            ["mdns", "services"],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);

        foreach (var address in AdbParsers.ParseMdnsAddresses(result.Output))
        {
            if (IsLegacyFixedPortAddress(address))
            {
                continue;
            }

            progress?.Report("Rokidに接続しています…");
            if (!await ConnectAsync(address, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (await IsRokidDeviceAsync(address, cancellationToken)
                    .ConfigureAwait(false) &&
                await IsSecureNetworkConnectionAsync(address, cancellationToken)
                    .ConfigureAwait(false))
            {
                return await UseSerialAsync(address, saveAddress: true)
                    .ConfigureAwait(false);
            }

            await RejectWifiDeviceAsync(
                address,
                removeSavedAddress: false,
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string?> GetSecureWifiAddressFromUsbAsync(
        string usbSerial,
        CancellationToken cancellationToken)
    {
        var port = await _adb.RunAsync(
            [
                "-s", usbSerial, "shell", "getprop", "service.adb.tls.port",
            ],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (!port.Succeeded)
        {
            return null;
        }

        var address = await _adb.RunAsync(
            [
                "-s", usbSerial, "shell", "ip", "-4", "-o", "addr",
                "show", "wlan0",
            ],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        return address.Succeeded
            ? ConnectionEncryption.BuildUsbTlsAddress(
                address.Output,
                port.Output)
            : null;
    }

    private async Task<bool> ClosePlaintextListenersAsync(
        string usbSerial,
        CancellationToken cancellationToken)
    {
        var openPorts = await ReadPlaintextListenerPortsAsync(
            usbSerial,
            cancellationToken).ConfigureAwait(false);
        if (openPorts is null)
        {
            return false;
        }

        if (openPorts.Count == 0)
        {
            return true;
        }

        _sawPlaintextListener = true;
        _ = await _adb.RunAsync(
            ["-s", usbSerial, "usb"],
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
            .ConfigureAwait(false);
        _ = await _adb.RunAsync(
            ["-s", usbSerial, "wait-for-device"],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        foreach (var property in PlaintextPortProperties)
        {
            _ = await _adb.RunAsync(
                ["-s", usbSerial, "shell", "setprop", property, "-1"],
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }

        var remaining = await ReadPlaintextListenerPortsAsync(
            usbSerial,
            cancellationToken).ConfigureAwait(false);
        return remaining is not null && remaining.Count == 0;
    }

    private async Task<IReadOnlyList<string>?> ReadPlaintextListenerPortsAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        foreach (var property in PlaintextPortProperties)
        {
            var result = await _adb.RunAsync(
                ["-s", serial, "shell", "getprop", property],
                TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return null;
            }

            values.AddRange(result.Output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries));
        }

        return ConnectionEncryption.ActiveListenerPorts(values);
    }

    private async Task<bool> IsSecureNetworkConnectionAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var plaintextPorts = await ReadPlaintextListenerPortsAsync(
            address,
            cancellationToken).ConfigureAwait(false);
        if (plaintextPorts is null)
        {
            return false;
        }

        var wireless = await _adb.RunAsync(
            [
                "-s", address, "shell", "settings", "get", "global",
                "adb_wifi_enabled",
            ],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (!wireless.Succeeded)
        {
            return false;
        }

        var wirelessEnabled = wireless.Output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Contains("1", StringComparer.Ordinal);
        var result = ConnectionEncryption.Inspect(
            address,
            plaintextPorts,
            wirelessEnabled);
        _sawPlaintextListener |= result.IsPlaintextListenerProblem;
        return result.IsEncrypted;
    }

    private static bool IsLegacyFixedPortAddress(string address) =>
        address.EndsWith(":5555", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> IsRokidDeviceAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var model = await _adb.RunAsync(
            ["-s", serial, "shell", "getprop", "ro.product.model"],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        var manufacturer = await _adb.RunAsync(
            ["-s", serial, "shell", "getprop", "ro.product.manufacturer"],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        return AdbParsers.IsRokidDevice(
            model.Output.Trim(),
            manufacturer.Output.Trim());
    }

    private async Task<bool> ConnectAsync(
        string address,
        CancellationToken cancellationToken)
    {
        _ = await _adb.RunAsync(
            ["connect", address],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        return await IsConnectedAsync(address, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsConnectedAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var result = await _adb.RunAsync(
            ["-s", serial, "get-state"],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        return string.Equals(
            result.Output.Trim(),
            "device",
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> UseSerialAsync(
        string serial,
        bool saveAddress)
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _serial = serial;
        }
        finally
        {
            _stateLock.Release();
        }

        if (saveAddress)
        {
            var directory = Path.GetDirectoryName(_addressFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                _addressFile,
                serial + Environment.NewLine).ConfigureAwait(false);
        }

        return serial;
    }

    private async Task<string?> ReadSavedAddressAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_addressFile))
        {
            return null;
        }

        var value = (await File.ReadAllTextAsync(_addressFile, cancellationToken)
            .ConfigureAwait(false)).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private async Task RejectWifiDeviceAsync(
        string address,
        bool removeSavedAddress,
        CancellationToken cancellationToken)
    {
        _ = await _adb.RunAsync(
            ["disconnect", address],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (removeSavedAddress && File.Exists(_addressFile))
        {
            File.Delete(_addressFile);
        }
    }

    private async Task<string> RequireCurrentSerialAsync()
    {
        var serial = await GetCurrentSerialAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(serial))
        {
            throw new RokidConnectionException(RokidConnectionError.NoDevice);
        }

        return serial;
    }

    private async Task RequireSuccessAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await _adb.RunAsync(arguments, timeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new RokidConnectionException(RokidConnectionError.WatchdogFailed);
        }
    }

    private async Task RunHeartbeatAsync(
        PeriodicTimer timer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                var serial = await GetCurrentSerialAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(serial))
                {
                    continue;
                }

                _ = await _adb.RunAsync(
                    ["-s", serial, "shell", "touch", RemoteHeartbeat],
                    TimeSpan.FromSeconds(3),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task StopHeartbeatAsync()
    {
        if (_heartbeatCancellation is null)
        {
            return;
        }

        await _heartbeatCancellation.CancelAsync().ConfigureAwait(false);
        _heartbeatTimer?.Dispose();
        if (_heartbeatTask is not null)
        {
            await _heartbeatTask.ConfigureAwait(false);
        }

        _heartbeatCancellation.Dispose();
        _heartbeatCancellation = null;
        _heartbeatTimer = null;
        _heartbeatTask = null;
    }
}
