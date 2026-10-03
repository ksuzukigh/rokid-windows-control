using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RokidControl.App.Services;
using RokidControl.Core.Connections;
using RokidControl.Core.Navigation;
using RokidControl.Core.Processes;

if (args is ["--keyboard-regression", var keyboardSerial, var keyboardOutput])
    return await KeyboardRegression.RunAsync(keyboardSerial, keyboardOutput);

if (args is ["--prepare-ring" or "--check-ring", var ringAdb, var ringSerial, var ringOutput])
    return await RingCheck.RunAsync(ringAdb, ringSerial, ringOutput, args[0] == "--prepare-ring");

if (args is ["--render-ui", var renderOutput])
{
    Exception? error = null;
    var thread = new Thread(() =>
    {
        try
        {
            Directory.CreateDirectory(renderOutput);
            var app = new RokidControl.App.App(); app.InitializeComponent();
            var window = new RokidControl.App.MainWindow();
            ((ScrollViewer)window.FindName("ModePanel")).Visibility = System.Windows.Visibility.Collapsed;
            ((Grid)window.FindName("LivePanel")).Visibility = System.Windows.Visibility.Visible;
            var image = new BitmapImage(new Uri(Path.Combine(Path.GetDirectoryName(renderOutput)!, "hardware-test-20261003", "Applications.png")));
            ((Image)window.FindName("LiveImage")).Source = image;
            var root = (FrameworkElement)window.Content;
            foreach (var scale in new[] { 1d, 1.25, 1.5 })
            {
                root.Measure(new Size(440, 480)); root.Arrange(new Rect(0, 0, 440, 480)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(440 * scale), (int)(480 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(renderOutput, $"live-{scale * 100:0}.png")); encoder.Save(file);
            }
            app.Shutdown();
        }
        catch (Exception exception) { error = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if (error != null) throw error;
    Console.WriteLine("PASS UI render at 100%, 125%, 150%"); return 0;
}
if (args.Length != 1 && !(args.Length == 2 && args[1] == "--initial-camera")) { Console.WriteLine("Usage: HardwareProbe <output-directory> [--initial-camera]"); return 2; }
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var resources = AppResources.Locate();
var adb = new AdbClient(resources.AdbPath, new ProcessRunner(resources.CreateEnvironment()));
using var logger = new AppLogger(Path.Combine(output, "session.log"));
await using var connection = new RokidConnectionManager(adb, Path.Combine(output, "wifi-address.txt"), resources.WatchdogPath);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
var ct = timeout.Token;
var serial = await connection.ConnectForStartupAsync(cancellationToken: ct);
Console.WriteLine($"PASS connection {serial}");
await connection.StartWindowsModeAsync(ct);
Console.WriteLine("PASS watchdog and configured R08 recovery");
using var input = new PersistentAdbInputSession(resources.AdbPath, serial, resources.CreateEnvironment());
async Task<string> Shell(string command)
{
    var result = await adb.RunAsync(["-s", serial, "shell", command], TimeSpan.FromSeconds(10), ct);
    if (!result.Succeeded) throw new IOException(result.CombinedOutput);
    return result.Output;
}
async Task Screenshot(string name)
{
    var remote = $"/data/local/tmp/rokid-control-test-{name}.png";
    await Shell($"screencap -p {remote}");
    var result = await adb.RunAsync(["-s", serial, "pull", remote, Path.Combine(output, name + ".png")], TimeSpan.FromSeconds(10), ct);
    if (!result.Succeeded) throw new IOException(result.CombinedOutput);
    await Shell($"rm -f {remote}");
}
if (args.Length == 2)
{
    await Shell("am start -n com.rokid.os.sprite.assistserver/com.rokid.os.sprite.assist.media.page.CameraActivity");
    await Task.Delay(700, ct);
    using var initial = new LiveSessionController(resources, logger, 480, 640, connection.IsOriginalCameraForegroundAsync);
    var initialFrames = 0;
    var initialSource = LiveDisplaySource.CameraWithHud;
    initial.FrameReady += (_, _) => Interlocked.Increment(ref initialFrames);
    initial.DisplaySourceChanged += value => initialSource = value;
    await initial.StartAsync(serial, new Progress<string>(Console.WriteLine), ct);
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while ((initialSource != LiveDisplaySource.OriginalCameraScreen || initialFrames < 3) && DateTime.UtcNow < deadline) await Task.Delay(200, ct);
    if (initialSource != LiveDisplaySource.OriginalCameraScreen || initialFrames < 3) throw new IOException("Initial camera frames missing");
    Console.WriteLine("PASS start with native camera already open");
    await input.SendKeyEventAsync("KEYCODE_BACK", ct);
    deadline = DateTime.UtcNow.AddSeconds(25);
    while (initialSource != LiveDisplaySource.CameraWithHud && DateTime.UtcNow < deadline) await Task.Delay(200, ct);
    if (initialSource != LiveDisplaySource.CameraWithHud) throw new IOException("Initial camera did not restore");
    Console.WriteLine("PASS initial camera close restores live");
    initial.Dispose();
    await connection.StopWindowsModeAsync(ct);
    return 0;
}
foreach (var shortcut in new[] { LauncherShortcut.Memo, LauncherShortcut.Home, LauncherShortcut.Applications })
{
    await input.OpenLauncherShortcutAsync(shortcut, 480, 640, ct);
    await Task.Delay(600, ct);
    await Screenshot(shortcut.ToString());
    Console.WriteLine($"PASS dynamic {shortcut}");
}
await input.SendKeyEventAsync("KEYCODE_DPAD_RIGHT", ct);
await Screenshot("AppsRight");
await input.SendKeyEventAsync("KEYCODE_ENTER", ct);
await input.SendKeyEventAsync("KEYCODE_BACK", ct);
await input.OpenLauncherShortcutAsync(LauncherShortcut.Home, 480, 640, ct);
var services = await Shell("settings get secure enabled_accessibility_services");
if (!services.Contains("com.anezium.r08accessbridge")) throw new IOException("R08 service lost after UI read");
Console.WriteLine("PASS UI read preserves R08 accessibility");
var volume = await Shell("settings get system volume_music_speaker");
var brightness = await Shell("settings get system screen_brightness");
try
{
    foreach (var activity in new[] { "volume.SettingVolumeActivity", "brightness.SettingBrightnessActivity" })
    {
        await Shell($"am start -n com.rokid.os.sprite.launcher/.page.{activity}");
        await Task.Delay(400, ct);
        if (!await input.IsSystemAdjustmentActiveAsync(ct)) throw new IOException("Adjustment activity not recognized");
        var keyboard = new KeyboardCommandProcessor(input, 480, 640);
        await keyboard.HandleAsync(KeyboardCommand.Right, ct);
        await Screenshot(activity.Split('.')[0]);
        await keyboard.HandleAsync(KeyboardCommand.Left, ct);
        Console.WriteLine($"PASS adjustment arrows {activity}");
    }
}
finally
{
    if (int.TryParse(volume.Trim(), out var originalVolume)) await Shell($"settings put system volume_music_speaker {originalVolume}");
    if (int.TryParse(brightness.Trim(), out var originalBrightness)) await Shell($"settings put system screen_brightness {originalBrightness}");
    await input.WakeHomeAsync(ct);
}
using var live = new LiveSessionController(resources, logger, 480, 640, connection.IsOriginalCameraForegroundAsync);
var frames = 0;
var source = LiveDisplaySource.CameraWithHud;
Exception? failure = null;
live.FrameReady += (_, _) => Interlocked.Increment(ref frames);
live.Failed += ex => failure = ex;
live.DisplaySourceChanged += value => source = value;
await live.StartAsync(serial, new Progress<string>(Console.WriteLine), ct);
async Task WaitFor(Func<bool> condition, string label)
{
    var until = DateTime.UtcNow.AddSeconds(25);
    while (!condition() && DateTime.UtcNow < until)
    {
        if (failure != null) throw failure;
        await Task.Delay(200, ct);
    }
    if (!condition()) throw new TimeoutException(label);
    Console.WriteLine("PASS " + label);
}
await WaitFor(() => Volatile.Read(ref frames) >= 3, "live camera and HUD frames");
foreach (var package in new[] { "io.github.ksuzukigh.rokidzoomincamera", "com.rokid.os.sprite.assistserver" })
{
    if (package == "com.rokid.os.sprite.assistserver")
        await Shell("am start -n com.rokid.os.sprite.assistserver/com.rokid.os.sprite.assist.media.page.CameraActivity");
    else await Shell($"monkey -p {package} -c android.intent.category.LAUNCHER 1");
    await WaitFor(() => source == LiveDisplaySource.OriginalCameraScreen, package + " color switch");
    var before = frames;
    await WaitFor(() => frames >= before + 3, package + " color frames");
    await Screenshot(package.Split('.').Last());
    await input.SendKeyEventAsync("KEYCODE_BACK", ct);
    await Task.Delay(500, ct);
    await input.WakeHomeAsync(ct);
    await WaitFor(() => source == LiveDisplaySource.CameraWithHud, package + " live restore");
    before = frames;
    await WaitFor(() => frames >= before + 3, package + " restored frames");
}
live.Dispose();
using (var standard = new ScrcpyProcessManager(resources, logger))
{
    standard.StartStandard(serial);
    using var process = System.Diagnostics.Process.GetProcessById(standard.ProcessId);
    var handle = await RokidControl.WindowsCapture.WindowHandleFinder.WaitForMainWindowAsync(process, TimeSpan.FromSeconds(15), ct);
    foreach (var package in new[] { "io.github.ksuzukigh.rokidzoomincamera", "com.rokid.os.sprite.assistserver" })
    {
        await Shell(package == "com.rokid.os.sprite.assistserver"
            ? "am start -n com.rokid.os.sprite.assistserver/com.rokid.os.sprite.assist.media.page.CameraActivity"
            : $"monkey -p {package} -c android.intent.category.LAUNCHER 1");
        await Task.Delay(1200, ct);
        var frame = await RokidControl.WindowsCapture.GraphicsCaptureProbe.CaptureOneFrameAsync(handle, TimeSpan.FromSeconds(10), ct);
        if (frame.Width < 400 || frame.Height < 500) throw new IOException("Standard camera frame missing");
        Console.WriteLine($"PASS standard {package} capture {frame.Width}x{frame.Height} checksum={frame.Checksum:X8}");
        await input.SendKeyEventAsync("KEYCODE_BACK", ct);
        await Task.Delay(500, ct);
        await input.WakeHomeAsync(ct);
    }
}
await connection.StopWindowsModeAsync(ct);
Console.WriteLine("PASS session shutdown");
return 0;
