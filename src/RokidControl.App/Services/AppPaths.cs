using System.IO;

namespace RokidControl.App.Services;

internal static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rokid Control");

    public static string LogsDirectory { get; } =
        Path.Combine(DataDirectory, "Logs");

    public static string LogFile { get; } =
        Path.Combine(LogsDirectory, "Rokid Control.log");

    public static string WifiAddressFile { get; } =
        Path.Combine(DataDirectory, "wifi-address.txt");

    public static string LiveVisibilityFile { get; } =
        Path.Combine(DataDirectory, "live-visibility.txt");
}
