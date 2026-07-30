using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RokidControl.WindowsCapture;

namespace RokidControl.App.Services;

internal sealed class ScrcpyProcessManager : IDisposable
{
    private const int MaximumRecentOutputLines = 20;
    private readonly AppResources _resources;
    private readonly AppLogger _logger;
    private readonly object _outputLock = new();
    private readonly Queue<string> _recentOutput = new();
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
                "--stay-awake",
                "--screen-off-timeout=86400",
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
                "--stay-awake",
                "--screen-off-timeout=86400",
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
        nint windowHandle;
        try
        {
            windowHandle = await WindowHandleFinder.WaitForMainWindowAsync(
                process,
                TimeSpan.FromSeconds(12),
                cancellationToken);
        }
        catch (TimeoutException exception)
        {
            LogRecentOutput("scrcpy起動待機タイムアウト");
            throw new TimeoutException(
                "Rokid画面の受信を開始できませんでした。Rokidが起動中で、Wi-Fiに接続されていることを確認して、もう一度試してください。",
                exception);
        }

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
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
        process.OutputDataReceived += Process_OutputDataReceived;
        process.ErrorDataReceived += Process_ErrorDataReceived;
        process.Exited += Process_Exited;
        if (!process.Start())
        {
            process.OutputDataReceived -= Process_OutputDataReceived;
            process.ErrorDataReceived -= Process_ErrorDataReceived;
            process.Dispose();
            throw new InvalidOperationException("Rokid画面を開始できませんでした。");
        }

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
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
            _process.OutputDataReceived -= Process_OutputDataReceived;
            _process.ErrorDataReceived -= Process_ErrorDataReceived;
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

    private void Process_OutputDataReceived(
        object sender,
        DataReceivedEventArgs e) =>
        RecordOutput("stdout", e.Data);

    private void Process_ErrorDataReceived(
        object sender,
        DataReceivedEventArgs e) =>
        RecordOutput("stderr", e.Data);

    private void RecordOutput(string stream, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_outputLock)
        {
            _recentOutput.Enqueue($"{stream}: {line}");
            while (_recentOutput.Count > MaximumRecentOutputLines)
            {
                _recentOutput.Dequeue();
            }
        }

        _logger.Log($"scrcpy {stream}: {line}");
    }

    private void LogRecentOutput(string prefix)
    {
        string output;
        lock (_outputLock)
        {
            output = _recentOutput.Count == 0
                ? "(出力なし)"
                : string.Join(" | ", _recentOutput);
        }

        _logger.Log($"{prefix}: {output}");
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
