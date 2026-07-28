using System.Diagnostics;

internal static class WindowHandleFinder
{
    public static async Task<nint> WaitForMainWindowAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.StartNew();
        while (startedAt.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "The target process exited before creating a window.");
            }

            if (process.MainWindowHandle != nint.Zero)
            {
                return process.MainWindowHandle;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new TimeoutException(
            "The target process did not create a main window.");
    }
}
