using System.Diagnostics;
using System.Runtime.InteropServices;
using RokidControl.WindowsCapture;

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

    public event EventHandler<ScrcpyExitedEventArgs>? Exited;

    public int ProcessId =>
        _process?.Id ??
        throw new InvalidOperationException("scrcpyは起動していません。");

    public void StartStandard(string serial)
    {
        Start(
            "背景なし",
            serial,
            [
                "--no-audio",
                "--keyboard=disabled",
                "--window-title=Rokid AI Glasses RV101（Windows操作モード）",
            ]);
    }

    public void StartLiveHud(string serial, int width, int height)
    {
        Start(
            "ライブHUD",
            serial,
            [
                "--no-audio",
                "--max-fps=15",
                "--keyboard=disabled",
                "--window-borderless",
                $"--window-width={width}",
                $"--window-height={height}",
                "--window-x=-30000",
                "--window-y=-30000",
                "--window-title=Rokid-Vision-HUD-Source",
            ]);
    }

    public void StartLiveCamera(string serial, int width, int height)
    {
        Start(
            "ライブカメラ",
            serial,
            [
                "--video-source=camera",
                "--camera-ar=4:3",
                "--max-size=640",
                "--camera-fps=15",
                "--orientation=270",
                "--no-audio",
                "--no-control",
                "--window-borderless",
                $"--window-width={width}",
                $"--window-height={height}",
                "--window-x=-30000",
                "--window-y=-30000",
                "--window-title=Rokid-Vision-Camera-Source",
            ]);
    }

    public async Task<bool> ActivateWindowAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var process = _process ??
            throw new InvalidOperationException("scrcpyは起動していません。");
        var windowHandle = await WindowHandleFinder.WaitForMainWindowAsync(
            process,
            TimeSpan.FromSeconds(8),
            cancellationToken);
        _ = NativeMethods.ShowWindow(windowHandle, 5);
        return NativeMethods.SetForegroundWindow(windowHandle);
    }

    private void Start(
        string sessionName,
        string serial,
        IReadOnlyList<string> arguments)
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
        startInfo.ArgumentList.Add("--serial");
        startInfo.ArgumentList.Add(serial);
        foreach (var argument in arguments)
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
        _logger.Log($"scrcpy開始 mode={sessionName} pid={process.Id}");
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
        var exitCode = sender is Process process
            ? process.ExitCode
            : -1;
        _logger.Log($"scrcpy終了 exitCode={exitCode}");
        Exited?.Invoke(this, new ScrcpyExitedEventArgs(exitCode));
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(
            nint windowHandle,
            int command);
    }
}

internal sealed class ScrcpyExitedEventArgs(int exitCode) : EventArgs
{
    public int ExitCode { get; } = exitCode;
}
