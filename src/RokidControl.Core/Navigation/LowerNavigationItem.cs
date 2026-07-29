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

    public static DevicePoint GetHighlightPoint(
        this LowerNavigationItem item,
        int screenWidth,
        int screenHeight)
    {
        var tapPoint = item.GetDevicePoint(screenWidth, screenHeight);

        // The visible lower-row icons on the 480x640 Rokid display are
        // centered at y=330, while their input target remains at y=320.
        return tapPoint with { Y = tapPoint.Y + screenHeight / 64 };
    }
}

public readonly record struct DevicePoint(int X, int Y);
