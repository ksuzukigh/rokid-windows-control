using RokidControl.Core.Connections;

namespace RokidControl.Core.Navigation;

public sealed class KeyboardCommandProcessor
{
    private readonly IRokidInputSession _input;
    private readonly int _screenWidth;
    private readonly int _screenHeight;
    private readonly Action<string>? _log;
    private bool _applicationMenuActive;

    public KeyboardCommandProcessor(
        IRokidInputSession input,
        int screenWidth,
        int screenHeight,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenHeight);

        _input = input;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _log = log;
    }

    public event Action<bool>? ApplicationMenuModeChanged;

    public async Task HandleAsync(
        KeyboardCommand command,
        CancellationToken cancellationToken = default)
    {
        switch (command)
        {
            case KeyboardCommand.Left:
                await SendApplicationMenuKeyAsync(
                    "KEYCODE_DPAD_LEFT",
                    cancellationToken).ConfigureAwait(false);
                break;
            case KeyboardCommand.Right:
                await SendApplicationMenuKeyAsync(
                    "KEYCODE_DPAD_RIGHT",
                    cancellationToken).ConfigureAwait(false);
                break;
            case KeyboardCommand.Enter:
                if (IsApplicationMenuActive())
                {
                    await SendKeyAsync(
                        "KEYCODE_ENTER",
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _log?.Invoke("Enterを無視（アプリ一覧の外）");
                }

                break;
            case KeyboardCommand.Back:
                await SendKeyAsync(
                    "KEYCODE_BACK",
                    cancellationToken).ConfigureAwait(false);
                break;
            case KeyboardCommand.Home:
                DeactivateApplicationMenu();
                await WakeAndTapAsync(
                    LauncherShortcut.Home,
                    cancellationToken).ConfigureAwait(false);
                break;
            case KeyboardCommand.Memo:
                DeactivateApplicationMenu();
                await WakeAndTapAsync(
                    LauncherShortcut.Memo,
                    cancellationToken).ConfigureAwait(false);
                break;
            case KeyboardCommand.Applications:
                DeactivateApplicationMenu();
                await WakeAndTapAsync(
                    LauncherShortcut.Applications,
                    cancellationToken).ConfigureAwait(false);
                ActivateApplicationMenu();
                break;
            case KeyboardCommand.Quit:
            default:
                break;
        }
    }

    public void ResetApplicationMenu()
    {
        DeactivateApplicationMenu();
    }

    private async Task SendApplicationMenuKeyAsync(
        string androidKey,
        CancellationToken cancellationToken)
    {
        if (!IsApplicationMenuActive())
        {
            _log?.Invoke($"方向キーを無視（アプリ一覧の外） {androidKey}");
            return;
        }

        await SendKeyAsync(androidKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task WakeAndTapAsync(
        LauncherShortcut shortcut,
        CancellationToken cancellationToken)
    {
        var point = shortcut.GetDevicePoint(_screenWidth, _screenHeight);
        await _input.WakeHomeAndTapAsync(
            point.X,
            point.Y,
            cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"{shortcut.GetTitle()}を開く");
    }

    private async Task SendKeyAsync(
        string androidKey,
        CancellationToken cancellationToken)
    {
        await _input.SendKeyEventAsync(androidKey, cancellationToken)
            .ConfigureAwait(false);
        _log?.Invoke($"キー {androidKey}");
    }

    private bool IsApplicationMenuActive()
    {
        return Volatile.Read(ref _applicationMenuActive);
    }

    private void ActivateApplicationMenu()
    {
        if (!Volatile.Read(ref _applicationMenuActive))
        {
            Volatile.Write(ref _applicationMenuActive, true);
            ApplicationMenuModeChanged?.Invoke(true);
        }
    }

    private void DeactivateApplicationMenu()
    {
        if (!Volatile.Read(ref _applicationMenuActive))
        {
            return;
        }

        Volatile.Write(ref _applicationMenuActive, false);
        ApplicationMenuModeChanged?.Invoke(false);
    }
}
