using System.Globalization;
using System.IO;

namespace RokidControl.App.Services;

internal static class AppPreferences
{
    public const double DefaultLiveVisibility = 0.9;

    public static double LoadLiveVisibility()
    {
        try
        {
            if (!File.Exists(AppPaths.LiveVisibilityFile))
            {
                return DefaultLiveVisibility;
            }

            var value = File.ReadAllText(AppPaths.LiveVisibilityFile);
            return double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? Math.Clamp(parsed, 0, 1)
                : DefaultLiveVisibility;
        }
        catch
        {
            return DefaultLiveVisibility;
        }
    }

    public static void SaveLiveVisibility(double value)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(
            AppPaths.LiveVisibilityFile,
            Math.Clamp(value, 0, 1).ToString(
                "0.00",
                CultureInfo.InvariantCulture));
    }
}
