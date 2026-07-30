using System.Diagnostics;
using RokidControl.Core.Imaging;
using RokidControl.WindowsCapture;

namespace RokidControl.App.Services;

internal sealed class LiveFrameEventArgs : EventArgs
{
    public LiveFrameEventArgs(BgraFrame frame)
    {
        Frame = frame;
    }

    public BgraFrame Frame { get; }
}

internal sealed class LiveSessionController : IDisposable
{
    private readonly AppResources _resources;
    private readonly AppLogger _logger;
    private readonly int _width;
    private readonly int _height;
    private readonly object _frameLock = new();
    private ScrcpyProcessManager? _hudProcess;
    private ScrcpyProcessManager? _cameraProcess;
    private WindowCaptureSession? _hudCapture;
    private WindowCaptureSession? _cameraCapture;
    private BgraFrame? _hudFrame;
    private BgraFrame? _cameraFrame;
    private long _frameVersion;
    private int _isComposing;
    private int _disposed;
    private double _visibility = 0.9;

    public LiveSessionController(
        AppResources resources,
        AppLogger logger,
        int width,
        int height)
    {
        _resources = resources;
        _logger = logger;
        _width = width;
        _height = height;
    }

    public event EventHandler<LiveFrameEventArgs>? FrameReady;

    public event Action<Exception>? Failed;

    public double Visibility
    {
        get => Volatile.Read(ref _visibility);
        set => Volatile.Write(ref _visibility, Math.Clamp(value, 0, 1));
    }

    public async Task StartAsync(
        string serial,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        progress.Report("カメラ映像を受信しています…");
        Exception? cameraError = null;
        for (var attempt = 1; attempt <= 15; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _cameraProcess = new ScrcpyProcessManager(
                    _resources,
                    _logger);
                _cameraProcess.StartLiveCamera(serial, _width, _height);
                _cameraCapture = await StartCaptureAsync(
                    _cameraProcess,
                    CameraCapture_FrameArrived,
                    cancellationToken);
                _cameraCapture.Failed += Capture_Failed;
                _cameraProcess.Exited += Process_Exited;
                _logger.Log(
                    $"ライブカメラ開始 attempt={attempt}");
                break;
            }
            catch (Exception exception) when (attempt < 15)
            {
                cameraError = exception;
                _logger.Log(
                    $"ライブカメラ待機 attempt={attempt} " +
                    $"{exception.Message}");
                DisposeCamera();
                progress.Report(
                    "撮影中です… カメラ映像の復帰を待っています");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        if (_cameraCapture is null)
        {
            throw cameraError ??
                new InvalidOperationException(
                    "カメラ映像を開始できませんでした。");
        }

        progress.Report("HUD画面を受信しています…");
        Exception? hudError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _hudProcess = new ScrcpyProcessManager(
                    _resources,
                    _logger);
                _hudProcess.StartLiveHud(serial, _width, _height);
                _hudCapture = await StartCaptureAsync(
                    _hudProcess,
                    HudCapture_FrameArrived,
                    cancellationToken);
                _hudCapture.Failed += Capture_Failed;
                _hudProcess.Exited += Process_Exited;
                _logger.Log($"ライブHUD開始 attempt={attempt}");
                break;
            }
            catch (Exception exception) when (attempt < 5)
            {
                hudError = exception;
                _logger.Log(
                    $"ライブHUD待機 attempt={attempt} " +
                    $"{exception.Message}");
                DisposeHud();
                progress.Report("HUD画面を再接続しています…");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        if (_hudCapture is null)
        {
            throw hudError ??
                new InvalidOperationException(
                    "HUD画面を開始できませんでした。");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeCamera();
        DisposeHud();
    }

    private static async Task<WindowCaptureSession> StartCaptureAsync(
        ScrcpyProcessManager processManager,
        EventHandler<CapturedFrameEventArgs> frameHandler,
        CancellationToken cancellationToken)
    {
        using var process = Process.GetProcessById(processManager.ProcessId);
        var windowHandle = await WindowHandleFinder.WaitForMainWindowAsync(
            process,
            TimeSpan.FromSeconds(10),
            cancellationToken);
        WindowHandleFinder.PrepareForBackgroundCapture(windowHandle);
        var capture = WindowCaptureSession.Start(windowHandle);
        capture.FrameArrived += frameHandler;

        var firstFrame =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        void FirstFrameHandler(
            object? sender,
            CapturedFrameEventArgs eventArguments) =>
            firstFrame.TrySetResult();

        capture.FrameArrived += FirstFrameHandler;
        try
        {
            await firstFrame.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
            return capture;
        }
        catch
        {
            capture.FrameArrived -= frameHandler;
            capture.Dispose();
            throw;
        }
        finally
        {
            capture.FrameArrived -= FirstFrameHandler;
        }
    }

    private void HudCapture_FrameArrived(
        object? sender,
        CapturedFrameEventArgs eventArguments)
    {
        lock (_frameLock)
        {
            _hudFrame = eventArguments.Frame;
            _frameVersion++;
        }

        ScheduleComposition();
    }

    private void CameraCapture_FrameArrived(
        object? sender,
        CapturedFrameEventArgs eventArguments)
    {
        lock (_frameLock)
        {
            _cameraFrame = eventArguments.Frame;
            _frameVersion++;
        }

        ScheduleComposition();
    }

    private void ScheduleComposition()
    {
        if (Interlocked.CompareExchange(ref _isComposing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(ComposeLatestFrame);
    }

    private void ComposeLatestFrame()
    {
        long composedVersion = -1;
        try
        {
            BgraFrame? hud;
            BgraFrame? camera;
            lock (_frameLock)
            {
                hud = _hudFrame;
                camera = _cameraFrame;
                composedVersion = _frameVersion;
            }

            if (hud is null || camera is null)
            {
                return;
            }

            var visibility = Visibility;
            var result = HudCompositor.Compose(
                camera,
                hud,
                visibility,
                0,
                _width,
                _height);
            if (Volatile.Read(ref _disposed) == 0)
            {
                FrameReady?.Invoke(this, new LiveFrameEventArgs(result));
            }
        }
        catch (Exception exception)
        {
            RaiseFailure(exception);
        }
        finally
        {
            Volatile.Write(ref _isComposing, 0);
            lock (_frameLock)
            {
                if (_frameVersion != composedVersion &&
                    Volatile.Read(ref _disposed) == 0)
                {
                    ScheduleComposition();
                }
            }
        }
    }

    private void Capture_Failed(
        object? sender,
        CaptureFailedEventArgs eventArguments) =>
        RaiseFailure(eventArguments.Exception);

    private void Process_Exited(object? sender, EventArgs eventArguments)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            RaiseFailure(
                new InvalidOperationException(
                    "ライブ映像の受信が停止しました。"));
        }
    }

    private void RaiseFailure(Exception exception)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Failed?.Invoke(exception);
        }
    }

    private void DisposeCamera()
    {
        if (_cameraCapture is not null)
        {
            _cameraCapture.FrameArrived -= CameraCapture_FrameArrived;
            _cameraCapture.Failed -= Capture_Failed;
            _cameraCapture.Dispose();
            _cameraCapture = null;
        }

        if (_cameraProcess is not null)
        {
            _cameraProcess.Exited -= Process_Exited;
            _cameraProcess.Dispose();
            _cameraProcess = null;
        }
    }

    private void DisposeHud()
    {
        if (_hudCapture is not null)
        {
            _hudCapture.FrameArrived -= HudCapture_FrameArrived;
            _hudCapture.Failed -= Capture_Failed;
            _hudCapture.Dispose();
            _hudCapture = null;
        }

        if (_hudProcess is not null)
        {
            _hudProcess.Exited -= Process_Exited;
            _hudProcess.Dispose();
            _hudProcess = null;
        }
    }
}
