namespace RokidControl.Core.Navigation;

public enum LauncherShortcut
{
    Memo = -1,
    Home = 0,
    Applications = 1,
}

public static class LauncherShortcutExtensions
{
    public static string GetTitle(this LauncherShortcut shortcut) =>
        shortcut switch
        {
            LauncherShortcut.Memo => "メモ",
            LauncherShortcut.Home => "Home",
            LauncherShortcut.Applications => "アプリ一覧",
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut)),
        };

    public static DevicePoint GetDevicePoint(
        this LauncherShortcut shortcut,
        int screenWidth,
        int screenHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenHeight);

        return new DevicePoint(
            screenWidth / 2 + (int)shortcut * (screenWidth / 15),
            screenHeight / 2);
    }
}

public readonly record struct DevicePoint(int X, int Y);
