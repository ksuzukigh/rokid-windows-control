using RokidControl.Core.Processes;

namespace RokidControl.Core.Connections;

public interface IAdbClient
{
    Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

