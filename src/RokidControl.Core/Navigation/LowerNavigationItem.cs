namespace RokidControl.Core.Navigation;

public enum LowerNavigationItem
{
    Memo = 0,
    Home = 1,
    Applications = 2,
}

public static class LowerNavigationItemExtensions
{
    public static string GetTitle(this LowerNavigationItem item) =>
        item switch
        {
            LowerNavigationItem.Memo => "メモ",
            LowerNavigationItem.Home => "Home",
            LowerNavigationItem.Applications => "アプリ一覧",
            _ => throw new ArgumentOutOfRangeException(nameof(item)),
        };

    public static int GetHorizontalOffset(
        this LowerNavigationItem item,
        int screenWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenWidth);
        return ((int)item - (int)LowerNavigationItem.Home) * (screenWidth / 15);
    }

    public static DevicePoint GetDevicePoint(
        this LowerNavigationItem item,
        int screenWidth,
        int screenHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenHeight);

        return new DevicePoint(
            screenWidth / 2 + item.GetHorizontalOffset(screenWidth),
            screenHeight / 2);
    }
}

public readonly record struct DevicePoint(int X, int Y);

