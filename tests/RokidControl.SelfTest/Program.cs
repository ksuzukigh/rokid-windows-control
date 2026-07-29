using RokidControl.Core.Connections;
using RokidControl.Core.Imaging;
using RokidControl.Core.Navigation;
using RokidControl.Core.Processes;

var tests = new (string Name, Action Run)[]
{
    ("下段ナビゲーション", TestKeyboardNavigation),
    ("ADB devices解析", TestAdbDevices),
    ("ADB mDNS解析", TestMdns),
    ("画面サイズ解析", TestScreenSize),
    ("IPv4解析", TestIpv4),
    ("Rokid機種判定", TestRokidIdentity),
    ("HUD合成", TestHudComposition),
    ("Windowsキー割り当て", TestWindowsKeyMapping),
    ("接続済みWi-Fi ADB再利用", () =>
        TestConnectedWifiReuseAsync().GetAwaiter().GetResult()),
    ("USB切断後の保存済みWi-Fi再利用", () =>
        TestSavedWifiAfterUsbDisconnectAsync().GetAwaiter().GetResult()),
};

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

Console.WriteLine($"{tests.Length}件のセルフテストが成功しました。");
return 0;

static void TestKeyboardNavigation()
{
    var state = new KeyboardNavigationState();
    Assert(!state.IsLowerRow, "初期状態は上段");
    AssertEqual(LowerNavigationItem.Home, state.EnterLowerRow(), "下段の初期項目");
    AssertEqual(LowerNavigationItem.Memo, state.MoveLowerRow(-1), "左移動");
    AssertEqual(LowerNavigationItem.Memo, state.MoveLowerRow(-1), "左端で停止");
    AssertEqual(LowerNavigationItem.Home, state.MoveLowerRow(1), "中央へ移動");
    AssertEqual(LowerNavigationItem.Applications, state.MoveLowerRow(1), "右移動");
    AssertEqual(
        LowerNavigationItem.Applications,
        state.MoveLowerRow(1),
        "右端で停止");
    Assert(state.LeaveLowerRow(), "下段から上段へ戻る");
    Assert(!state.LeaveLowerRow(), "すでに上段なら変更なし");

    AssertEqual(
        -32,
        LowerNavigationItem.Memo.GetHorizontalOffset(480),
        "メモの水平位置");
    AssertEqual(
        0,
        LowerNavigationItem.Home.GetHorizontalOffset(480),
        "Homeの水平位置");
    AssertEqual(
        32,
        LowerNavigationItem.Applications.GetHorizontalOffset(480),
        "アプリ一覧の水平位置");
    AssertEqual(
        new DevicePoint(240, 320),
        LowerNavigationItem.Home.GetDevicePoint(480, 640),
        "Homeの座標");
}

static void TestAdbDevices()
{
    const string output = """
        List of devices attached
        ABC123	device product:rv101 model:RG_Glasses
        192.168.1.20:5555	device
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

    var invisible = HudCompositor.Compose(camera, hud, 0, 0);
    AssertEqual(camera.Pixels[center + 1], invisible.Pixels[center + 1], "非表示HUD");

    var visible = HudCompositor.Compose(camera, hud, 1, 0);
    AssertEqual((byte)255, visible.Pixels[center + 1], "緑HUD");
    AssertEqual(camera.Pixels[0], visible.Pixels[0], "黒HUD領域");

    var thickened = HudCompositor.Compose(camera, hud, 1, 0.5);
    AssertEqual((byte)255, thickened.Pixels[1], "太さ反映");
    AssertEqual(3, thickened.Width, "出力幅");
    AssertEqual(3, thickened.Height, "出力高さ");
}

static void TestWindowsKeyMapping()
{
    AssertEqual(
        KeyboardCommand.Left,
        WindowsKeyCommandMapper.Map(0x25, false, false),
        "左キー");
    AssertEqual(
        KeyboardCommand.CenterTap,
        WindowsKeyCommandMapper.Map(0x20, false, false),
        "Space");
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
        WindowsKeyCommandMapper.Map('A', false, false),
        "未割り当てキー");
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
                "192.168.1.20:5555",
                serial,
                "接続済みWi-Fiシリアル");
        }

        AssertEqual(
            "192.168.1.20:5555",
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
        const string savedAddress = "192.168.1.20:5555";
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
                "List of devices attached\n192.168.1.20:5555\tdevice\n",
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
                "List of devices attached\n192.168.1.20:5555\tdevice\n",
            ["devices"] =>
                "List of devices attached\n",
            ["connect", "192.168.1.20:5555"] =>
                ConnectSavedAddress(),
            ["-s", "192.168.1.20:5555", "get-state"] when WifiConnected =>
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
        return "connected to 192.168.1.20:5555\n";
    }
}
