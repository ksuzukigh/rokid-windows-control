namespace RokidControl.Core.Connections;

public interface IRokidInputSession : IDisposable
{
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
