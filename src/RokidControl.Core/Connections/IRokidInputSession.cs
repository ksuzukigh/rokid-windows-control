namespace RokidControl.Core.Connections;

public interface IRokidInputSession : IDisposable
{
    Task<bool> IsSystemAdjustmentActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    Task OpenLauncherShortcutAsync(
        RokidControl.Core.Navigation.LauncherShortcut shortcut,
        int width, int height, CancellationToken cancellationToken = default)
    {
        var point = RokidControl.Core.Navigation.LauncherShortcutExtensions.GetDevicePoint(shortcut, width, height);
        return WakeHomeAndTapAsync(point.X, point.Y, cancellationToken);
    }

    Task SendKeyEventAsync(
        string androidKey,
        CancellationToken cancellationToken = default);

    Task TapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default);

    Task WakeHomeAndTapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default);

    Task WakeHomeAsync(
        CancellationToken cancellationToken = default);

    Task<bool> IsLauncherActiveAsync(
        CancellationToken cancellationToken = default);
}
