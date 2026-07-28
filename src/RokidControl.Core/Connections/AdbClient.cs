using RokidControl.Core.Processes;

namespace RokidControl.Core.Connections;

public sealed class AdbClient : IAdbClient
{
    private readonly string _adbPath;
    private readonly ProcessRunner _runner;

    public AdbClient(string adbPath, ProcessRunner runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        _adbPath = adbPath;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        _runner.RunAsync(_adbPath, arguments, timeout, cancellationToken);
}

