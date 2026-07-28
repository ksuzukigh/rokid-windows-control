using Windows.Graphics.Capture;

Console.WriteLine($"OS: {Environment.OSVersion.VersionString}");
Console.WriteLine($"Windows Graphics Capture supported: {GraphicsCaptureSession.IsSupported()}");

return GraphicsCaptureSession.IsSupported() ? 0 : 1;
