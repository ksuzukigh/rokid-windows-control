namespace RokidControl.Core.Processes;

public sealed class RapidFailureGuard
{
    private readonly int _maximumFailures;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _failures = new();

    public RapidFailureGuard(int maximumFailures, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFailures);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        _maximumFailures = maximumFailures;
        _window = window;
    }

    public bool RecordFailure(DateTimeOffset occurredAt)
    {
        var cutoff = occurredAt - _window;
        while (_failures.TryPeek(out var failure) && failure < cutoff)
        {
            _failures.Dequeue();
        }

        _failures.Enqueue(occurredAt);
        return _failures.Count >= _maximumFailures;
    }

    public void Reset()
    {
        _failures.Clear();
    }
}
