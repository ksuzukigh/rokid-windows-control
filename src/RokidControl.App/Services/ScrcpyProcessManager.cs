using System.Diagnostics;

namespace RokidControl.App.Services;

internal sealed class ScrcpyProcessManager : IDisposable
{
    private readonly AppResources _resources;
    private readonly AppLogger _logger;
    private Process? _process;
    private bool _disposed;

    public ScrcpyProcessManager(AppResources resources, AppLogger logger)
    {
        _resources = resources;
        _logger = logger;
    }

    public event EventHandler? Exited;

    public int ProcessId =>
        _process?.Id ??
        throw new InvalidOperationException("scrcpyは起動していません。");

    public void StartStandard(string serial)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("scrcpyはすでに起動しています。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _resources.ScrcpyPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _resources.VendorDirectory,
        };
        foreach (var argument in new[]
                 {
                     "--serial", serial,
                     "--no-audio",
                     "--keyboard=disabled",
                     "--window-title=Rokid AI Glasses RV101（Windows操作モード）",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var item in _resources.CreateEnvironment())
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        process.Exited += Process_Exited;
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Rokid画面を開始できませんでした。");
        }

        _process = process;
        _logger.Log($"scrcpy開始 pid={process.Id} serial={serial}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is not null)
        {
            _process.Exited -= Process_Exited;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2_000);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }

            _process.Dispose();
            _process = null;
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        _logger.Log("scrcpy終了");
        Exited?.Invoke(this, EventArgs.Empty);
    }
}
