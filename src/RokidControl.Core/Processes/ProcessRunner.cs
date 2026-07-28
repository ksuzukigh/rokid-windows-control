using System.Diagnostics;

namespace RokidControl.Core.Processes;

public sealed class ProcessRunner
{
    private readonly IReadOnlyDictionary<string, string> _environment;

    public ProcessRunner(
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _environment = environment ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach (var item in _environment)
        {
            process.StartInfo.Environment[item.Key] = item.Value;
        }

        if (!process.Start())
        {
            return new CommandResult(-1, string.Empty, "プロセスを開始できませんでした。", false);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CommandResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false),
            timedOut);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }
}

