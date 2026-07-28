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

    private readonly IAdbClient _adb;
    private readonly string _addressFile;
    private readonly string _watchdogFile;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private PeriodicTimer? _heartbeatTimer;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;
    private string _serial = string.Empty;

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
        progress?.Report("Rokidを探しています…");

        var usb = await FindUsbRokidAsync(cancellationToken).ConfigureAwait(false);
        if (usb is not null)
        {
            progress?.Report("Rokidに接続しています…");
            return await UseSerialAsync(usb, saveAddress: false).ConfigureAwait(false);
        }

        var saved = await ReadSavedAddressAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null && await ConnectAsync(saved, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report("Rokidに接続しています…");
            if (await IsRokidDeviceAsync(saved, cancellationToken).ConfigureAwait(false))
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
                progress?.Report("RokidのWi-Fiを準備しています…");
                return await RecoverWifiUsingUsbAsync(
                    usb,
                    cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RokidConnectionException(RokidConnectionError.NoDevice);
    }

    public async Task<string?> ReconnectAsync(
        CancellationToken cancellationToken = default)
    {
        var previous = await GetCurrentSerialAsync().ConfigureAwait(false);
        if (previous.Contains(':', StringComparison.Ordinal))
        {
            _ = await _adb.RunAsync(
                ["disconnect", previous],
                TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var usb = await FindUsbRokidAsync(cancellationToken).ConfigureAwait(false);
            if (usb is not null)
            {
                return await UseSerialAsync(usb, saveAddress: false).ConfigureAwait(false);
            }

            if (previous.Contains(':', StringComparison.Ordinal) &&
                await ConnectAsync(previous, cancellationToken).ConfigureAwait(false))
            {
                if (await IsRokidDeviceAsync(previous, cancellationToken).ConfigureAwait(false))
                {
                    return await UseSerialAsync(previous, saveAddress: true)
                        .ConfigureAwait(false);
                }

                await RejectWifiDeviceAsync(
                    previous,
                    removeSavedAddress: true,
                    cancellationToken).ConfigureAwait(false);
                previous = string.Empty;
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
            if (await IsRokidDeviceAsync(device.Serial, cancellationToken)
                    .ConfigureAwait(false))
            {
                return device.Serial;
            }
        }

        return null;
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
            progress?.Report("Rokidに接続しています…");
            if (!await ConnectAsync(address, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (await IsRokidDeviceAsync(address, cancellationToken)
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

    private async Task<string> RecoverWifiUsingUsbAsync(
        string usbSerial,
        CancellationToken cancellationToken)
    {
        if (!await IsRokidDeviceAsync(usbSerial, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new RokidConnectionException(RokidConnectionError.NoDevice);
        }

        var status = await GetWifiStatusAsync(usbSerial, cancellationToken)
            .ConfigureAwait(false);
        var openedSettings = false;
        if (status.Contains("Wifi is disabled", StringComparison.Ordinal))
        {
            await RunInputKeyAsync(usbSerial, "KEYCODE_WAKEUP", cancellationToken)
                .ConfigureAwait(false);
            _ = await _adb.RunAsync(
                ["-s", usbSerial, "shell", "wm", "dismiss-keyguard"],
                TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false);
            _ = await _adb.RunAsync(
                [
                    "-s", usbSerial, "shell", "am", "start", "-a",
                    "android.settings.WIFI_SETTINGS",
                ],
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
                await RunInputKeyAsync(usbSerial, "KEYCODE_WAKEUP", cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken)
                    .ConfigureAwait(false);
                await RunInputKeyAsync(usbSerial, "KEYCODE_ENTER", cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
                status = await GetWifiStatusAsync(usbSerial, cancellationToken)
                    .ConfigureAwait(false);
                if (!status.Contains("Wifi is disabled", StringComparison.Ordinal))
                {
                    break;
                }
            }

            openedSettings = true;
        }

        string? ipAddress = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            status = await GetWifiStatusAsync(usbSerial, cancellationToken)
                .ConfigureAwait(false);
            var addressResult = await _adb.RunAsync(
                ["-s", usbSerial, "shell", "ip", "-4", "addr", "show", "wlan0"],
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            ipAddress = AdbParsers.ParseIpv4Address(addressResult.Output);
            if (ipAddress is not null &&
                status.Contains("Wifi is connected to", StringComparison.Ordinal))
            {
                break;
            }

            ipAddress = null;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        if (ipAddress is null)
        {
            throw new RokidConnectionException(RokidConnectionError.WifiUnavailable);
        }

        if (openedSettings)
        {
            await RunInputKeyAsync(usbSerial, "KEYCODE_WAKEUP", cancellationToken)
                .ConfigureAwait(false);
            await RunInputKeyAsync(usbSerial, "KEYCODE_HOME", cancellationToken)
                .ConfigureAwait(false);
        }

        var address = $"{ipAddress}:5555";
        _ = await _adb.RunAsync(
            ["disconnect", address],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        _ = await _adb.RunAsync(
            ["-s", usbSerial, "tcpip", "5555"],
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
            .ConfigureAwait(false);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await ConnectAsync(address, cancellationToken).ConfigureAwait(false))
            {
                if (await IsRokidDeviceAsync(address, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return await UseSerialAsync(address, saveAddress: true)
                        .ConfigureAwait(false);
                }

                await RejectWifiDeviceAsync(
                    address,
                    removeSavedAddress: false,
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RokidConnectionException(RokidConnectionError.NoDevice);
    }

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

    private async Task<string> GetWifiStatusAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var result = await _adb.RunAsync(
            ["-s", serial, "shell", "cmd", "wifi", "status"],
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
        return result.CombinedOutput;
    }

    private async Task RunInputKeyAsync(
        string serial,
        string key,
        CancellationToken cancellationToken)
    {
        _ = await _adb.RunAsync(
            ["-s", serial, "shell", "input", "keyevent", key],
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
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
