using System.Diagnostics;
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

if (args.Length != 2 ||
    !string.Equals(args[0], "--pid", StringComparison.OrdinalIgnoreCase) ||
    !int.TryParse(args[1], out var processId))
{
    Console.Error.WriteLine(
        "Usage: RokidControl.CaptureProbe [--pid <process-id>]");
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
        $"after {result.Elapsed.TotalMilliseconds:F0} ms");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 3;
}
