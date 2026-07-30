using RokidControl.Core.Connections;
using RokidControl.Core.Imaging;
using RokidControl.Core.Navigation;
using RokidControl.Core.Processes;

var tests = new List<(string Name, Action Run)>
{
    ("直接ショートカット座標", TestShortcutCoordinates),
    ("ADB devices解析", TestAdbDevices),
    ("ADB mDNS解析", TestMdns),
    ("画面サイズ解析", TestScreenSize),
    ("IPv4解析", TestIpv4),
    ("Rokid機種判定", TestRokidIdentity),
    ("HUD合成", TestHudComposition),
    ("Windowsキー割り当て", TestWindowsKeyMapping),
    ("キーボード入力処理", () =>
        TestKeyboardCommandProcessorAsync().GetAwaiter().GetResult()),
    ("キーボード入力障害後の継続", () =>
        TestKeyboardCommandRecoveryAsync().GetAwaiter().GetResult()),
    ("短時間の連続障害停止", TestRapidFailureGuard),
    ("外部キャンセル時の子プロセス終了", () =>
        TestProcessRunnerCancellationAsync().GetAwaiter().GetResult()),
    ("背景なし画面終了判定", TestStandardSessionExitPolicy),
    ("接続済みWi-Fi ADB再利用", () =>
        TestConnectedWifiReuseAsync().GetAwaiter().GetResult()),
    ("USB切断後の保存済みWi-Fi再利用", () =>
        TestSavedWifiAfterUsbDisconnectAsync().GetAwaiter().GetResult()),
    ("USBから暗号化Wi-Fiへの移行", () =>
        TestUsbToSecureWifiHandoffAsync().GetAwaiter().GetResult()),
    ("再接続時のUSBから暗号化Wi-Fiへの移行", () =>
        TestReconnectUsbToSecureWifiAsync().GetAwaiter().GetResult()),
    ("旧5555番Wi-Fi接続の拒否", () =>
        TestLegacyFixedWifiRejectedAsync().GetAwaiter().GetResult()),
    ("無線準備失敗時のUSB継続", () =>
        TestDelayedUsbKeepsUsbTransportAsync().GetAwaiter().GetResult()),
};

if (args is ["--adb-input", var adbPath, var serial])
{
    tests.Add(
        ("持続ADB入力実機", () =>
            TestPersistentAdbInputAsync(adbPath, serial)
                .GetAwaiter()
                .GetResult()));
}
else if (args is ["--adb-usb-handoff", var handoffAdbPath])
{
    tests.Add(
        ("USBから暗号化Wi-Fiへの実機移行", () =>
            TestSecureWifiHandoffOnDeviceAsync(handoffAdbPath)
                .GetAwaiter()
                .GetResult()));
}

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count}件のセルフテストが失敗しました。");
    return 1;
}

Console.WriteLine($"{tests.Count}件のセルフテストが成功しました。");
return 0;

static void TestShortcutCoordinates()
{
    AssertEqual(
        new DevicePoint(208, 320),
        LauncherShortcut.Memo.GetDevicePoint(480, 640),
        "メモの座標");
    AssertEqual(
        new DevicePoint(240, 320),
        LauncherShortcut.Home.GetDevicePoint(480, 640),
        "Homeの座標");
    AssertEqual(
        new DevicePoint(272, 320),
        LauncherShortcut.Applications.GetDevicePoint(480, 640),
        "アプリ一覧の座標");
}

static void TestAdbDevices()
{
    const string output = """
        List of devices attached
        ABC123	device product:rv101 model:RG_Glasses
        192.168.1.20:41001	device
        XYZ789	unauthorized

        """;
    var devices = AdbParsers.ParseDevices(output);
    AssertEqual(3, devices.Count, "機器数");
    Assert(devices[0].IsReady && devices[0].IsUsb, "USB機器");
    Assert(devices[1].IsReady && devices[1].IsNetwork, "Wi-Fi機器");
    Assert(!devices[2].IsReady, "未承認機器");
}

static void TestMdns()
{
    const string output = """
        adb-ABC123-vWxy._adb-tls-connect._tcp. _adb-tls-connect._tcp 192.168.1.20:40123
        adb-ABC123-vWxy._adb-tls-connect._tcp. _adb-tls-connect._tcp 192.168.1.20:40123
        adb-ABC123-vWxy._adb-tls-pairing._tcp. _adb-tls-pairing._tcp 192.168.1.20:39999
        """;
    var addresses = AdbParsers.ParseMdnsAddresses(output);
    AssertEqual(1, addresses.Count, "重複を除いたアドレス数");
    AssertEqual("192.168.1.20:40123", addresses[0], "接続アドレス");
}

static void TestScreenSize()
{
    AssertEqual(
        (480, 640),
        AdbParsers.ParseScreenSize("Physical size: 480x640"),
        "x表記");
    AssertEqual(
        (480, 640),
        AdbParsers.ParseScreenSize("Override size: 480 × 640"),
        "乗算記号表記");
    AssertEqual<(int Width, int Height)?>(null, AdbParsers.ParseScreenSize("unknown"), "不明");
}

static void TestIpv4()
{
    const string output = """
        4: wlan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500
            inet 192.168.10.42/24 brd 192.168.10.255 scope global wlan0
        """;
    AssertEqual("192.168.10.42", AdbParsers.ParseIpv4Address(output), "IPv4");
    AssertEqual<string?>(null, AdbParsers.ParseIpv4Address("inet6 fe80::1/64"), "IPv6のみ");
}

static void TestRokidIdentity()
{
    Assert(AdbParsers.IsRokidDevice("RV101", "unknown"), "RV101");
    Assert(AdbParsers.IsRokidDevice("RG-Glasses", "unknown"), "RG-Glasses");
    Assert(AdbParsers.IsRokidDevice("Smart Glasses", "Rokid"), "メーカー");
    Assert(!AdbParsers.IsRokidDevice("Pixel 10", "Google"), "他社端末を拒否");
}

static void TestHudComposition()
{
    var camera = SolidFrame(3, 3, 10, 20, 30);
    var hudPixels = new byte[3 * 3 * 4];
    var center = ((1 * 3) + 1) * 4;
    hudPixels[center + 1] = 255;
    hudPixels[center + 3] = 255;
    var hud = new BgraFrame(3, 3, hudPixels);

    var minimum = HudCompositor.Compose(camera, hud, 0, 0);
    Assert(
        minimum.Pixels[center + 1] > camera.Pixels[center + 1],
        "最小値でもHUDを表示");
    Assert(
        minimum.Pixels[center + 1] < 255,
        "最小値は従来より薄く表示");

    var visible = HudCompositor.Compose(camera, hud, 1, 0);
    AssertEqual((byte)255, visible.Pixels[center + 1], "緑HUD");
    AssertEqual(camera.Pixels[0], visible.Pixels[0], "黒HUD領域");
    AssertEqual(
        camera.Pixels[1],
        visible.Pixels[1],
        "実用範囲では輪郭をにじませない");

    var thickened = HudCompositor.Compose(camera, hud, 1, 0.5);
    AssertEqual((byte)255, thickened.Pixels[1], "太さ反映");
    AssertEqual(3, thickened.Width, "出力幅");
    AssertEqual(3, thickened.Height, "出力高さ");

    var dimHudPixels = new byte[3 * 3 * 4];
    dimHudPixels[center + 1] = 10;
    dimHudPixels[center + 3] = 255;
    var dimHud = new BgraFrame(3, 3, dimHudPixels);
    var natural = HudCompositor.Compose(camera, dimHud, 0, 0);
    var enhanced = HudCompositor.Compose(camera, dimHud, 1, 0);
    Assert(
        natural.Pixels[center + 1] > camera.Pixels[center + 1],
        "最小値でも元のHUDを表示");
    Assert(
        enhanced.Pixels[center + 1] > natural.Pixels[center + 1],
        "見やすさ設定で暗いHUDを増幅");

    var wideCamera = SolidFrame(4, 2, 10, 20, 30);
    var tallHud = SolidFrame(2, 4, 0, 16, 0);
    var normalized = HudCompositor.Compose(
        wideCamera,
        tallHud,
        0.5,
        0.625,
        3,
        3);
    AssertEqual(3, normalized.Width, "アスペクトフィル出力幅");
    AssertEqual(3, normalized.Height, "アスペクトフィル出力高さ");
}

static void TestWindowsKeyMapping()
{
    AssertEqual(
        KeyboardCommand.Left,
        WindowsKeyCommandMapper.Map(0x25, false, false),
        "左キー");
    AssertEqual<KeyboardCommand?>(
        null,
        WindowsKeyCommandMapper.Map(0x20, false, false),
        "Spaceは割り当てない");
    AssertEqual(
        KeyboardCommand.Home,
        WindowsKeyCommandMapper.Map('H', false, false),
        "HでHome");
    AssertEqual(
        KeyboardCommand.Memo,
        WindowsKeyCommandMapper.Map('M', false, false),
        "Mでメモ");
    AssertEqual(
        KeyboardCommand.Applications,
        WindowsKeyCommandMapper.Map('A', false, false),
        "Aでアプリ一覧");
    AssertEqual(
        KeyboardCommand.Quit,
        WindowsKeyCommandMapper.Map('Q', true, false),
        "Ctrl+Q");
    AssertEqual<KeyboardCommand?>(
        KeyboardCommand.Quit,
        WindowsKeyCommandMapper.Map(0x73, false, true),
        "Alt+F4で終了");
    AssertEqual<KeyboardCommand?>(
        null,
        WindowsKeyCommandMapper.Map('Z', false, false),
        "未割り当てキー");
    AssertEqual(
        "M メモ　H Home　A アプリ",
        NavigationHintText.Normal,
        "通常案内はRokid下段と同じ順");
    AssertEqual(
        "← → 選択　Enter 決定　Esc 戻る",
        NavigationHintText.ApplicationMenu,
        "アプリ選択中の案内");
}

static async Task TestKeyboardCommandProcessorAsync()
{
    var input = new RecordingInputSession();
    var processor = new KeyboardCommandProcessor(input, 480, 640);
    var applicationMenuModes = new List<bool>();
    processor.ApplicationMenuModeChanged += applicationMenuModes.Add;

    await processor.HandleAsync(KeyboardCommand.Down);
    await processor.HandleAsync(KeyboardCommand.Right);
    await processor.HandleAsync(KeyboardCommand.Up);
    await processor.HandleAsync(KeyboardCommand.Enter);
    AssertEqual(0, input.Keys.Count, "アプリ一覧の外では方向キーとEnterを送らない");

    await processor.HandleAsync(KeyboardCommand.Memo);
    AssertEqual(
        new DevicePoint(208, 320),
        input.WakeHomeTaps.Single(),
        "Mはメモを直接開く");

    await processor.HandleAsync(KeyboardCommand.Home);
    AssertEqual(
        new DevicePoint(240, 320),
        input.WakeHomeTaps.Last(),
        "Hはどの位置からでもHomeを直接選ぶ");
    AssertEqual(0, input.WakeHomeRequests, "Hは選択が残るHomeキー単独を使わない");

    await processor.HandleAsync(KeyboardCommand.Applications);
    AssertEqual(
        new DevicePoint(272, 320),
        input.WakeHomeTaps.Last(),
        "Aはアプリ一覧を直接開く");
    AssertEqual(true, applicationMenuModes.Last(), "Aでアプリ選択モード開始");

    await processor.HandleAsync(KeyboardCommand.Left);
    await processor.HandleAsync(KeyboardCommand.Right);
    AssertEqual(2, input.Keys.Count, "アプリ一覧では左右キーを送る");
    AssertEqual("KEYCODE_DPAD_LEFT", input.Keys[0], "左キー");
    AssertEqual("KEYCODE_DPAD_RIGHT", input.Keys[1], "右キー");
    await processor.HandleAsync(KeyboardCommand.Enter);
    AssertEqual("KEYCODE_ENTER", input.Keys.Last(), "Enterでアプリを決定");
    AssertEqual(false, applicationMenuModes.Last(), "決定後は選択モード終了");

    var keyCountAfterEnter = input.Keys.Count;
    await processor.HandleAsync(KeyboardCommand.Right);
    AssertEqual(keyCountAfterEnter, input.Keys.Count, "決定後の方向キーは送らない");

    await processor.HandleAsync(KeyboardCommand.Back);
    AssertEqual("KEYCODE_BACK", input.Keys.Last(), "Escは常に戻る");
}

static async Task TestKeyboardCommandRecoveryAsync()
{
    var input = new RecordingInputSession
    {
        FailNextKey = true,
    };
    var processor = new KeyboardCommandProcessor(input, 480, 640);
    await processor.HandleAsync(KeyboardCommand.Applications);

    try
    {
        await processor.HandleAsync(KeyboardCommand.Right);
        throw new InvalidOperationException("最初の入力は失敗する必要があります。");
    }
    catch (IOException)
    {
        // Expected simulated transport failure.
    }

    await processor.HandleAsync(KeyboardCommand.Left);
    AssertEqual(
        "KEYCODE_DPAD_LEFT",
        input.Keys.Single(),
        "一時障害後も次の入力を処理");
}

static void TestRapidFailureGuard()
{
    var guard = new RapidFailureGuard(2, TimeSpan.FromSeconds(5));
    var start = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    Assert(!guard.RecordFailure(start), "最初の障害では継続");
    Assert(
        guard.RecordFailure(start.AddSeconds(4)),
        "5秒以内の2回目で停止");

    guard.Reset();
    Assert(!guard.RecordFailure(start), "リセット後は再開可能");
    Assert(
        !guard.RecordFailure(start.AddSeconds(6)),
        "5秒を超えた障害は連続扱いにしない");
}

static async Task TestProcessRunnerCancellationAsync()
{
    var pidFile = Path.Combine(
        Path.GetTempPath(),
        $"rokid-process-self-test-{Guid.NewGuid():N}.txt");
    try
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new ProcessRunner();
        var runTask = runner.RunAsync(
            "powershell.exe",
            [
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                $"[IO.File]::WriteAllText('{pidFile.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 60",
            ],
            TimeSpan.FromMinutes(2),
            cancellation.Token);

        for (var attempt = 0; attempt < 200 && !File.Exists(pidFile); attempt++)
        {
            await Task.Delay(50);
        }

        Assert(File.Exists(pidFile), "子プロセスの開始を確認");
        var processId = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        cancellation.Cancel();

        try
        {
            await runTask;
            throw new InvalidOperationException(
                "外部キャンセルはOperationCanceledExceptionになる必要があります。");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        await Task.Delay(100);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            Assert(process.HasExited, "キャンセル後に子プロセスが残存");
        }
        catch (ArgumentException)
        {
            // The process no longer exists.
        }
    }
    finally
    {
        File.Delete(pidFile);
    }
}

static void TestStandardSessionExitPolicy()
{
    AssertEqual(
        StandardSessionExitAction.Quit,
        StandardSessionExitPolicy.Decide(0, connectionAlive: true),
        "利用者が画面を閉じた場合は終了");
    AssertEqual(
        StandardSessionExitAction.Reconnect,
        StandardSessionExitPolicy.Decide(1, connectionAlive: true),
        "異常終了は再接続");
    AssertEqual(
        StandardSessionExitAction.Reconnect,
        StandardSessionExitPolicy.Decide(0, connectionAlive: false),
        "通信切断時は再接続");
}

static async Task TestConnectedWifiReuseAsync()
{
    var addressFile = Path.Combine(
        Path.GetTempPath(),
        $"rokid-wifi-self-test-{Guid.NewGuid():N}.txt");
    try
    {
        var manager = new RokidConnectionManager(
            new ConnectedWifiAdbClient(),
            addressFile,
            "unused-watchdog.sh");
        await using (manager.ConfigureAwait(false))
        {
            var serial = await manager.ConnectForStartupAsync(
                searchDuration: TimeSpan.FromMilliseconds(50));
            AssertEqual(
                "192.168.1.20:41001",
                serial,
                "接続済みWi-Fiシリアル");
        }

        AssertEqual(
            "192.168.1.20:41001",
            (await File.ReadAllTextAsync(addressFile)).Trim(),
            "Wi-Fiアドレス保存");
    }
    finally
    {
        File.Delete(addressFile);
    }
}

static async Task TestSavedWifiAfterUsbDisconnectAsync()
{
    var addressFile = Path.Combine(
        Path.GetTempPath(),
        $"rokid-wifi-reconnect-self-test-{Guid.NewGuid():N}.txt");
    try
    {
        const string savedAddress = "192.168.1.20:41001";
        await File.WriteAllTextAsync(addressFile, savedAddress);
        var adb = new UsbThenSavedWifiAdbClient();
        var manager = new RokidConnectionManager(
            adb,
            addressFile,
            "unused-watchdog.sh");
        await using (manager.ConfigureAwait(false))
        {
            var usbSerial = await manager.ConnectForStartupAsync(
                searchDuration: TimeSpan.FromMilliseconds(50));
            AssertEqual("USB123", usbSerial, "初期USBシリアル");

            adb.UsbConnected = false;
            var reconnected = await manager.ReconnectAsync();
            AssertEqual(
                savedAddress,
                reconnected,
                "USB切断後の保存済みWi-Fiシリアル");
            Assert(adb.SavedAddressConnectAttempted, "保存済みアドレスへ接続");
        }
    }
    finally
    {
        File.Delete(addressFile);
    }
}

static async Task TestDelayedUsbKeepsUsbTransportAsync()
{
    var addressFile = Path.Combine(
        Path.GetTempPath(),
        $"rokid-delayed-usb-self-test-{Guid.NewGuid():N}.txt");
    try
    {
        var adb = new DelayedUsbAdbClient();
        var manager = new RokidConnectionManager(
            adb,
            addressFile,
            "unused-watchdog.sh");
        await using (manager.ConfigureAwait(false))
        {
            var serial = await manager.ConnectForStartupAsync(
                searchDuration: TimeSpan.FromSeconds(2));
            AssertEqual("USB123", serial, "待機中に見つけたUSBシリアル");
        }

        Assert(!adb.TcpipAttempted, "USB検出時にtcpipを実行しない");
        Assert(!File.Exists(addressFile), "USBシリアルをWi-Fi接続先として保存しない");
    }
    finally
    {
        File.Delete(addressFile);
    }
}

static async Task TestUsbToSecureWifiHandoffAsync()
{
    var addressFile = Path.Combine(
        Path.GetTempPath(),
        $"rokid-secure-wifi-self-test-{Guid.NewGuid():N}.txt");
    try
    {
        const string secureAddress = "192.168.1.20:40123";
        var adb = new UsbToSecureWifiAdbClient(secureAddress);
        var manager = new RokidConnectionManager(
            adb,
            addressFile,
            "unused-watchdog.sh");
        await using (manager.ConfigureAwait(false))
        {
            var serial = await manager.ConnectForStartupAsync(
                searchDuration: TimeSpan.FromMilliseconds(50));
            AssertEqual(secureAddress, serial, "暗号化Wi-Fiシリアル");
        }

        Assert(adb.RokidWifiEnabled, "RokidのWi-Fi設定を有効にする");
        Assert(adb.AndroidWifiEnabled, "AndroidのWi-Fiを有効にする");
        Assert(adb.SecureWirelessAdbEnabled, "暗号化Wi-Fi ADBを有効にする");
        Assert(adb.SecureAddressConnected, "動的TLSアドレスへ接続する");
        Assert(!adb.TcpipAttempted, "tcpipを実行しない");
        AssertEqual(
            secureAddress,
            (await File.ReadAllTextAsync(addressFile)).Trim(),
            "確認済み暗号化Wi-Fiアドレス");
    }
    finally
    {
        File.Delete(addressFile);
    }
}

static async Task TestReconnectUsbToSecureWifiAsync()
{
    var addressFile = Path.Combine(
        Path.GetTempPath(),
        $"rokid-reconnect-secure-wifi-{Guid.NewGuid():N}.txt");
    try
    {
        const string initialAddress = "192.168.1.20:40123";
        const string recoveredAddress = "192.168.1.20:40234";
        var adb = new ReconnectUsbToSecureWifiAdbClient(
            initialAddress,
            recoveredAddress);
        var manager = new RokidConnectionManager(
            adb,
            addressFile,
            "unused-watchdog.sh");
        await using (manager.ConfigureAwait(false))
        {
            var initial = await manager.ConnectForStartupAsync(
                searchDuration: TimeSpan.FromMilliseconds(50));
            AssertEqual(initialAddress, initial, "初期Wi-Fiシリアル");

            adb.RecoveryMode = true;
            var recovered = await manager.ReconnectAsync();
            AssertEqual(recoveredAddress, recovered, "再接続後のTLSシリアル");
        }

        Assert(adb.SecureWirelessAdbEnabled, "再接続時にもTLSを有効にする");
        Assert(adb.RecoveredAddressConnected, "再接続時の動的TLSへ接続する");
        AssertEqual(
            recoveredAddress,
            (await File.ReadAllTextAsync(addressFile)).Trim(),
            "再接続後のTLSアドレス");
    }
    finally
    {
        File.Delete(addressFile);
    }
}

static async Task TestLegacyFixedWifiRejectedAsync()
{
    var addressFile = Path.Combine(
        Path.GetTempPath(),
        $"rokid-legacy-wifi-{Guid.NewGuid():N}.txt");
    try
    {
        const string legacyAddress = "192.168.1.20:5555";
        const string secureAddress = "192.168.1.20:40345";
        await File.WriteAllTextAsync(addressFile, legacyAddress);
        var adb = new LegacyFixedWifiAdbClient(
            legacyAddress,
            secureAddress);
        var manager = new RokidConnectionManager(
            adb,
            addressFile,
            "unused-watchdog.sh");
        await using (manager.ConfigureAwait(false))
        {
            var serial = await manager.ConnectForStartupAsync(
                searchDuration: TimeSpan.FromSeconds(2));
            AssertEqual(secureAddress, serial, "旧接続を除外したTLSシリアル");
        }

        Assert(adb.LegacyAddressDisconnected, "旧5555番接続を切断する");
        Assert(!adb.LegacyAddressConnectAttempted, "旧5555番へ再接続しない");
        AssertEqual(
            secureAddress,
            (await File.ReadAllTextAsync(addressFile)).Trim(),
            "旧接続をTLSアドレスで置き換える");
    }
    finally
    {
        File.Delete(addressFile);
    }
}

static async Task TestSecureWifiHandoffOnDeviceAsync(string adbPath)
{
    var addressFile = Path.Combine(
        Path.GetTempPath(),
        $"rokid-device-handoff-{Guid.NewGuid():N}.txt");
    try
    {
        var adb = new AdbClient(adbPath, new ProcessRunner());
        var devicesResult = await adb.RunAsync(
            ["devices"],
            TimeSpan.FromSeconds(5));
        var devices = AdbParsers.ParseDevices(devicesResult.Output);
        Assert(
            devices.Any(device => device.IsReady && device.IsUsb),
            "USB接続中のRokidが必要です");

        foreach (var networkDevice in devices.Where(
                     device => device.IsReady && device.IsNetwork))
        {
            _ = await adb.RunAsync(
                ["disconnect", networkDevice.Serial],
                TimeSpan.FromSeconds(5));
        }

        var manager = new RokidConnectionManager(
            adb,
            addressFile,
            "unused-watchdog.sh");
        await using (manager.ConfigureAwait(false))
        {
            await manager.PrepareAdbServerAsync();
            var serial = await manager.ConnectForStartupAsync(
                searchDuration: TimeSpan.FromSeconds(30));
            Assert(
                serial.Contains(':', StringComparison.Ordinal),
                $"USBから無線へ切り替わりませんでした: {serial}");
            Assert(
                !serial.EndsWith(":5555", StringComparison.Ordinal),
                $"固定5555番ポートへ接続しました: {serial}");
            AssertEqual(
                serial,
                (await File.ReadAllTextAsync(addressFile)).Trim(),
                "実機で確認した動的TLSアドレス");

            var reconnected = await manager.ReconnectAsync();
            if (reconnected is null)
            {
                throw new InvalidOperationException(
                    "再接続時にUSBから無線へ戻りませんでした。");
            }

            Assert(
                reconnected.Contains(':', StringComparison.Ordinal),
                $"再接続時にUSBから無線へ戻りませんでした: {reconnected}");
            Assert(
                !reconnected.EndsWith(":5555", StringComparison.Ordinal),
                $"再接続時に固定5555番ポートを使用しました: {reconnected}");
            AssertEqual(
                reconnected,
                (await File.ReadAllTextAsync(addressFile)).Trim(),
                "再接続時に確認した動的TLSアドレス");
        }
    }
    finally
    {
        File.Delete(addressFile);
    }
}

static async Task TestPersistentAdbInputAsync(string adbPath, string serial)
{
    using var input = new PersistentAdbInputSession(adbPath, serial);
    await input.VerifyReadyAsync();
    _ = await input.IsLauncherActiveAsync();

    var elapsed = new List<TimeSpan>();
    for (var index = 0; index < 5; index++)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        await input.SendKeyEventAsync("KEYCODE_UNKNOWN");
        elapsed.Add(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
    }

    var averageMilliseconds = elapsed.Average(item => item.TotalMilliseconds);
    Assert(
        averageMilliseconds < 300,
        $"持続ADB入力が遅すぎます（平均 {averageMilliseconds:F0}ms）");
    Console.WriteLine(
        $"INFO 持続ADB入力 平均={averageMilliseconds:F0}ms " +
        $"各回={string.Join(",", elapsed.Select(item => $"{item.TotalMilliseconds:F0}ms"))}");
}

static BgraFrame SolidFrame(int width, int height, byte blue, byte green, byte red)
{
    var pixels = new byte[width * height * 4];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
        pixels[index + 3] = 255;
    }

    return new BgraFrame(width, height, pixels);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{message}: expected={expected}, actual={actual}");
    }
}

sealed class ConnectedWifiAdbClient : IAdbClient
{
    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var values = arguments.ToArray();
        var output = values switch
        {
            ["devices"] =>
                "List of devices attached\n192.168.1.20:41001\tdevice\n",
            [.., "getprop", "ro.product.model"] => "RG-glasses\n",
            [.., "getprop", "ro.product.manufacturer"] => "Rokid\n",
            _ => string.Empty,
        };
        return Task.FromResult(new CommandResult(0, output, string.Empty, false));
    }
}

sealed class UsbThenSavedWifiAdbClient : IAdbClient
{
    public bool UsbConnected { get; set; } = true;

    public bool SavedAddressConnectAttempted { get; private set; }

    private bool WifiConnected { get; set; }

    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var values = arguments.ToArray();
        var output = values switch
        {
            ["devices"] when UsbConnected =>
                "List of devices attached\nUSB123\tdevice\n",
            ["devices"] when WifiConnected =>
                "List of devices attached\n192.168.1.20:41001\tdevice\n",
            ["devices"] =>
                "List of devices attached\n",
            ["connect", "192.168.1.20:41001"] =>
                ConnectSavedAddress(),
            ["-s", "192.168.1.20:41001", "get-state"] when WifiConnected =>
                "device\n",
            [.., "getprop", "ro.product.model"] => "RG-glasses\n",
            [.., "getprop", "ro.product.manufacturer"] => "Rokid\n",
            _ => string.Empty,
        };
        return Task.FromResult(new CommandResult(0, output, string.Empty, false));
    }

    private string ConnectSavedAddress()
    {
        SavedAddressConnectAttempted = true;
        WifiConnected = true;
        return "connected to 192.168.1.20:41001\n";
    }
}

sealed class DelayedUsbAdbClient : IAdbClient
{
    private int _deviceQueries;

    public bool TcpipAttempted { get; private set; }

    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = arguments.ToArray();
        if (values.Contains("tcpip", StringComparer.Ordinal))
        {
            TcpipAttempted = true;
            throw new InvalidOperationException("tcpip must not be executed");
        }

        var output = values switch
        {
            ["devices"] when ++_deviceQueries >= 3 =>
                "List of devices attached\nUSB123\tdevice\n",
            ["devices"] => "List of devices attached\n",
            ["mdns", "services"] => string.Empty,
            [.., "getprop", "ro.product.model"] => "RG-glasses\n",
            [.., "getprop", "ro.product.manufacturer"] => "Rokid\n",
            _ => string.Empty,
        };
        return Task.FromResult(new CommandResult(0, output, string.Empty, false));
    }
}

sealed class UsbToSecureWifiAdbClient(string secureAddress) : IAdbClient
{
    public bool RokidWifiEnabled { get; private set; }

    public bool AndroidWifiEnabled { get; private set; }

    public bool SecureWirelessAdbEnabled { get; private set; }

    public bool SecureAddressConnected { get; private set; }

    public bool TcpipAttempted { get; private set; }

    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = arguments.ToArray();
        if (values.Contains("tcpip", StringComparer.Ordinal))
        {
            TcpipAttempted = true;
            throw new InvalidOperationException("tcpip must not be executed");
        }

        var output = values switch
        {
            ["devices"] when SecureAddressConnected =>
                $"List of devices attached\nUSB123\tdevice\n{secureAddress}\tdevice\n",
            ["devices"] =>
                "List of devices attached\nUSB123\tdevice\n",
            ["-s", _, "shell", "am", "broadcast", ..] =>
                EnableRokidWifi(),
            ["-s", _, "shell", "cmd", "wifi", "set-wifi-enabled", "enabled"] =>
                EnableAndroidWifi(),
            ["-s", _, "shell", "settings", "put", "global", "adb_wifi_enabled", "1"] =>
                EnableSecureWirelessAdb(),
            ["-s", _, "shell", "settings", "get", "global", "adb_wifi_enabled"] =>
                SecureWirelessAdbEnabled ? "1\n" : "0\n",
            ["mdns", "services"] when SecureWirelessAdbEnabled =>
                $"adb-USB123-test._adb-tls-connect._tcp " +
                $"_adb-tls-connect._tcp {secureAddress}\n",
            ["connect", var address] when address == secureAddress =>
                ConnectSecureAddress(),
            ["-s", var address, "get-state"]
                when address == secureAddress && SecureAddressConnected =>
                "device\n",
            [.., "getprop", "ro.product.model"] => "RG-glasses\n",
            [.., "getprop", "ro.product.manufacturer"] => "Rokid\n",
            _ => string.Empty,
        };
        return Task.FromResult(new CommandResult(0, output, string.Empty, false));
    }

    private string EnableRokidWifi()
    {
        RokidWifiEnabled = true;
        return "Broadcast completed: result=0\n";
    }

    private string EnableAndroidWifi()
    {
        AndroidWifiEnabled = true;
        return string.Empty;
    }

    private string EnableSecureWirelessAdb()
    {
        SecureWirelessAdbEnabled = true;
        return string.Empty;
    }

    private string ConnectSecureAddress()
    {
        SecureAddressConnected = true;
        return $"connected to {secureAddress}\n";
    }
}

sealed class ReconnectUsbToSecureWifiAdbClient(
    string initialAddress,
    string recoveredAddress) : IAdbClient
{
    public bool RecoveryMode { get; set; }

    public bool SecureWirelessAdbEnabled { get; private set; }

    public bool RecoveredAddressConnected { get; private set; }

    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = arguments.ToArray();
        var output = values switch
        {
            ["devices"] when !RecoveryMode =>
                $"List of devices attached\n{initialAddress}\tdevice\n",
            ["devices"] when RecoveredAddressConnected =>
                $"List of devices attached\nUSB123\tdevice\n" +
                $"{recoveredAddress}\tdevice\n",
            ["devices"] =>
                "List of devices attached\nUSB123\tdevice\n",
            ["-s", _, "shell", "settings", "put", "global", "adb_wifi_enabled", "1"] =>
                EnableSecureWirelessAdb(),
            ["-s", _, "shell", "settings", "get", "global", "adb_wifi_enabled"] =>
                SecureWirelessAdbEnabled ? "1\n" : "0\n",
            ["mdns", "services"] when SecureWirelessAdbEnabled =>
                $"adb-USB123-recovery._adb-tls-connect._tcp " +
                $"_adb-tls-connect._tcp {recoveredAddress}\n",
            ["connect", var address] when address == recoveredAddress =>
                ConnectRecoveredAddress(),
            ["-s", var address, "get-state"]
                when address == recoveredAddress && RecoveredAddressConnected =>
                "device\n",
            [.., "getprop", "ro.product.model"] => "RG-glasses\n",
            [.., "getprop", "ro.product.manufacturer"] => "Rokid\n",
            _ => string.Empty,
        };
        return Task.FromResult(new CommandResult(0, output, string.Empty, false));
    }

    private string EnableSecureWirelessAdb()
    {
        SecureWirelessAdbEnabled = true;
        return string.Empty;
    }

    private string ConnectRecoveredAddress()
    {
        RecoveredAddressConnected = true;
        return $"connected to {recoveredAddress}\n";
    }
}

sealed class LegacyFixedWifiAdbClient(
    string legacyAddress,
    string secureAddress) : IAdbClient
{
    public bool LegacyAddressDisconnected { get; private set; }

    public bool LegacyAddressConnectAttempted { get; private set; }

    private bool SecureAddressConnected { get; set; }

    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = arguments.ToArray();
        if (values is ["connect", var legacyConnectAddress] &&
            legacyConnectAddress == legacyAddress)
        {
            LegacyAddressConnectAttempted = true;
        }

        var output = values switch
        {
            ["devices"] when !LegacyAddressDisconnected =>
                $"List of devices attached\n{legacyAddress}\tdevice\n",
            ["devices"] when SecureAddressConnected =>
                $"List of devices attached\n{secureAddress}\tdevice\n",
            ["devices"] => "List of devices attached\n",
            ["disconnect", var disconnectAddress]
                when disconnectAddress == legacyAddress =>
                DisconnectLegacyAddress(),
            ["mdns", "services"] =>
                $"adb-USB123-secure._adb-tls-connect._tcp " +
                $"_adb-tls-connect._tcp {secureAddress}\n",
            ["connect", var connectAddress] when connectAddress == secureAddress =>
                ConnectSecureAddress(),
            ["-s", var stateAddress, "get-state"]
                when stateAddress == secureAddress && SecureAddressConnected =>
                "device\n",
            [.., "getprop", "ro.product.model"] => "RG-glasses\n",
            [.., "getprop", "ro.product.manufacturer"] => "Rokid\n",
            _ => string.Empty,
        };
        return Task.FromResult(new CommandResult(0, output, string.Empty, false));
    }

    private string DisconnectLegacyAddress()
    {
        LegacyAddressDisconnected = true;
        return $"disconnected {legacyAddress}\n";
    }

    private string ConnectSecureAddress()
    {
        SecureAddressConnected = true;
        return $"connected to {secureAddress}\n";
    }
}

sealed class RecordingInputSession : IRokidInputSession
{
    public bool LauncherActive { get; set; }

    public bool FailNextKey { get; set; }

    public List<string> Keys { get; } = [];

    public List<DevicePoint> Taps { get; } = [];

    public List<DevicePoint> WakeHomeTaps { get; } = [];

    public int WakeHomeRequests { get; private set; }

    public Task SendKeyEventAsync(
        string androidKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextKey)
        {
            FailNextKey = false;
            throw new IOException("simulated failure");
        }

        Keys.Add(androidKey);
        return Task.CompletedTask;
    }

    public Task TapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Taps.Add(new DevicePoint(x, y));
        return Task.CompletedTask;
    }

    public Task WakeHomeAndTapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WakeHomeTaps.Add(new DevicePoint(x, y));
        return Task.CompletedTask;
    }

    public Task WakeHomeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WakeHomeRequests++;
        return Task.CompletedTask;
    }

    public Task<bool> IsLauncherActiveAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LauncherActive);
    }

    public void Dispose()
    {
    }
}
