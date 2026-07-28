using System.Diagnostics;
using RokidControl.Core.Imaging;
using RokidControl.WindowsCapture;
using Windows.Graphics.Capture;

Console.WriteLine($"OS: {Environment.OSVersion.VersionString}");
Console.WriteLine(
    $"Windows Graphics Capture supported: {GraphicsCaptureSession.IsSupported()}");

if (!GraphicsCaptureSession.IsSupported())
{
    return 1;
}

if (args.Length == 0)
{
    return 0;
}

if (args.Length == 1 &&
    string.Equals(
        args[0],
        "--benchmark-compose",
        StringComparison.OrdinalIgnoreCase))
{
    const int width = 480;
    const int height = 640;
    var cameraPixels = new byte[width * height * 4];
    var hudPixels = new byte[cameraPixels.Length];
    for (var index = 0; index < cameraPixels.Length; index += 4)
    {
        cameraPixels[index] = (byte)(index % 251);
        cameraPixels[index + 1] = (byte)((index / 3) % 251);
        cameraPixels[index + 2] = (byte)((index / 7) % 251);
        cameraPixels[index + 3] = 255;
        if ((index / 4) % 37 == 0)
        {
            hudPixels[index + 1] = 255;
            hudPixels[index + 3] = 255;
        }
    }

    var camera = new BgraFrame(width, height, cameraPixels);
    var hud = new BgraFrame(width, height, hudPixels);
    _ = HudCompositor.Compose(camera, hud, 0.9, 0.9);
    var timer = Stopwatch.StartNew();
    const int iterations = 5;
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        _ = HudCompositor.Compose(camera, hud, 0.9, 0.9);
    }

    timer.Stop();
    Console.WriteLine(
        $"Compose average: " +
        $"{timer.Elapsed.TotalMilliseconds / iterations:F1} ms");
    return 0;
}

if (args.Length == 4 &&
    string.Equals(args[0], "--compose", StringComparison.OrdinalIgnoreCase) &&
    int.TryParse(args[1], out var hudProcessId) &&
    int.TryParse(args[2], out var cameraProcessId) &&
    double.TryParse(
        args[3],
        System.Globalization.CultureInfo.InvariantCulture,
        out var visibility))
{
    try
    {
        using var hudProcess = Process.GetProcessById(hudProcessId);
        using var cameraProcess = Process.GetProcessById(cameraProcessId);
        var hudWindowTask = WindowHandleFinder.WaitForMainWindowAsync(
            hudProcess,
            TimeSpan.FromSeconds(15));
        var cameraWindowTask = WindowHandleFinder.WaitForMainWindowAsync(
            cameraProcess,
            TimeSpan.FromSeconds(15));
        await Task.WhenAll(hudWindowTask, cameraWindowTask);

        var hudFrameTask = GraphicsCaptureProbe.CaptureOneFrameAsync(
            await hudWindowTask,
            TimeSpan.FromSeconds(10));
        var cameraFrameTask = GraphicsCaptureProbe.CaptureOneFrameAsync(
            await cameraWindowTask,
            TimeSpan.FromSeconds(10));
        await Task.WhenAll(hudFrameTask, cameraFrameTask);
        var hudFrame = await hudFrameTask;
        var cameraFrame = await cameraFrameTask;
        var composeTimer = Stopwatch.StartNew();
        var composed = HudCompositor.Compose(
            cameraFrame.Frame,
            hudFrame.Frame,
            visibility,
            visibility);
        composeTimer.Stop();
        Console.WriteLine(
            $"HUD: {hudFrame.Width}x{hudFrame.Height}, " +
            $"checksum 0x{hudFrame.Checksum:X8}");
        Console.WriteLine(
            $"Camera: {cameraFrame.Width}x{cameraFrame.Height}, " +
            $"checksum 0x{cameraFrame.Checksum:X8}");
        Console.WriteLine(
            $"Composed: {composed.Width}x{composed.Height}, " +
            $"checksum 0x{CalculateChecksum(composed.Pixels):X8}, " +
            $"{composeTimer.Elapsed.TotalMilliseconds:F0} ms");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 3;
    }
}

if (args.Length != 2 ||
    !string.Equals(args[0], "--pid", StringComparison.OrdinalIgnoreCase) ||
    !int.TryParse(args[1], out var processId))
{
    Console.Error.WriteLine(
        "Usage: RokidControl.CaptureProbe [--pid <process-id> | " +
        "--compose <hud-pid> <camera-pid> <visibility>]");
    return 2;
}

try
{
    using var process = Process.GetProcessById(processId);
    var windowHandle = await WindowHandleFinder.WaitForMainWindowAsync(
        process,
        TimeSpan.FromSeconds(15));
    Console.WriteLine($"Window handle found: 0x{windowHandle:X}");

    var result = await GraphicsCaptureProbe.CaptureOneFrameAsync(
        windowHandle,
        TimeSpan.FromSeconds(10));
    Console.WriteLine(
        $"Frame received: {result.Width}x{result.Height}, " +
        $"{result.PixelBytes} BGRA bytes, " +
        $"checksum 0x{result.Checksum:X8}, " +
        $"after {result.Elapsed.TotalMilliseconds:F0} ms");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 3;
}

static uint CalculateChecksum(ReadOnlySpan<byte> pixels)
{
    const uint offsetBasis = 2166136261;
    const uint prime = 16777619;
    var value = offsetBasis;
    foreach (var pixel in pixels)
    {
        value ^= pixel;
        value *= prime;
    }

    return value;
}
