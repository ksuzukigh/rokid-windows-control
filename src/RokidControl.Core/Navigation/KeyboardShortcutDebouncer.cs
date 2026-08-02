namespace RokidControl.Core.Navigation;

public sealed class KeyboardShortcutDebouncer
{
    private readonly uint _windowMilliseconds;
    private readonly object _lock = new();
    private KeyboardCommand? _lastCommand;
    private uint _lastEventTime;

    public KeyboardShortcutDebouncer(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            window,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            window,
            TimeSpan.FromMilliseconds(uint.MaxValue));
        _windowMilliseconds = (uint)window.TotalMilliseconds;
    }

    public bool ShouldAccept(KeyboardCommand command, uint eventTime)
    {
        lock (_lock)
        {
            var elapsed = unchecked(eventTime - _lastEventTime);
            if (_lastCommand == command && elapsed <= _windowMilliseconds)
            {
                return false;
            }

            _lastCommand = command;
            _lastEventTime = eventTime;
            return true;
        }
    }
}
