using System.IO;
using RokidControl.Core.Connections;

namespace RokidControl.App.Services;

internal sealed record AppResources(
    string AdbPath,
    string ScrcpyPath,
    string ScrcpyServerPath,
    string WatchdogPath,
    string VendorDirectory)
{
    public static AppResources Locate()
    {
        var configured = Environment.GetEnvironmentVariable(
            "ROKID_CONTROL_SCRCPY_DIR");
        var vendorDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "vendor", "scrcpy")
            : Path.GetFullPath(configured);
        var watchdog = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "rokid_windows_wifi_watchdog.sh");
        var resources = new AppResources(
            Path.Combine(vendorDirectory, "adb.exe"),
            Path.Combine(vendorDirectory, "scrcpy.exe"),
            Path.Combine(vendorDirectory, "scrcpy-server"),
            watchdog,
            vendorDirectory);

        foreach (var path in new[]
                 {
                     resources.AdbPath,
                     resources.ScrcpyPath,
                     resources.ScrcpyServerPath,
                     resources.WatchdogPath,
                 })
        {
            if (!File.Exists(path))
            {
                throw new RokidConnectionException(
                    RokidConnectionError.MissingResource,
                    Path.GetFileName(path));
            }
        }

        return resources;
    }

    public IReadOnlyDictionary<string, string> CreateEnvironment() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ADB"] = AdbPath,
            ["SCRCPY_SERVER_PATH"] = ScrcpyServerPath,
            ["ANDROID_ADB_SERVER_PORT"] = "5037",
            ["PATH"] = VendorDirectory + Path.PathSeparator +
                Environment.GetEnvironmentVariable("PATH"),
        };
}
