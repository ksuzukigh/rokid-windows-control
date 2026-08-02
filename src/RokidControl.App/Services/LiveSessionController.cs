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

internal enum LiveDisplaySource
{
    CameraWithHud,
    OriginalCameraScreen,
}

internal sealed class LiveSessionController : IDisposable
{
    private readonly AppResources _resources;
    private readonly AppLogger _logger;
    private readonly int _width;
    private readonly int _height;
    private readonly Func<CancellationToken, Task<bool?>>
        _originalCameraForegroundProbe;
    private readonly object _frameLock = new();
    private ScrcpyProcessManager? _hudProcess;
    private ScrcpyProcessManager? _cameraProcess;
    private WindowCaptureSession? _hudCapture;
    private WindowCaptureSession? _cameraCapture;
    private BgraFrame? _hudFrame;
    private BgraFrame? _cameraFrame;
    private long _frameVersion;
    private int _isComposing;
    private int _cameraRecoveryActive;
    private int _disposed;
    private double _visibility = 0.9;
    private bool _isShowingOriginalCameraScreen;
    private string _serial = string.Empty;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _cameraRecoveryTask;

    public LiveSessionController(
        AppResources resources,
        AppLogger logger,
        int width,
        int height,
        Func<CancellationToken, Task<bool?>> originalCameraForegroundProbe)
    {
        _resources = resources;
        _logger = logger;
        _width = width;
        _height = height;
        _originalCameraForegroundProbe =
            originalCameraForegroundProbe ??
            throw new ArgumentNullException(
                nameof(originalCameraForegroundProbe));
    }

    public event EventHandler<LiveFrameEventArgs>? FrameReady;

    public event Action<Exception>? Failed;

    public event Action<LiveDisplaySource>? DisplaySourceChanged;

    public event Action<string?>? StatusChanged;

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
        _serial = serial;
        _sessionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = _sessionCancellation.Token;

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
                _cameraCapture.Failed += CameraCapture_Failed;
                _cameraProcess.Exited += CameraProcess_Exited;
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
                _hudCapture.Failed += HudCapture_Failed;
                _hudProcess.Exited += HudProcess_Exited;
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

        _sessionCancellation?.Cancel();
        DisposeCamera();
        DisposeHud();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
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
            bool showOriginalCameraScreen;
            lock (_frameLock)
            {
                hud = _hudFrame;
                camera = _cameraFrame;
                showOriginalCameraScreen =
                    _isShowingOriginalCameraScreen;
                composedVersion = _frameVersion;
            }

            if (hud is null ||
                (!showOriginalCameraScreen && camera is null))
            {
                return;
            }

            var result = showOriginalCameraScreen
                ? HudCompositor.ShowDeviceScreen(
                    hud,
                    _width,
                    _height)
                : HudCompositor.Compose(
                    camera!,
                    hud,
                    Visibility,
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

    private void CameraCapture_Failed(
        object? sender,
        CaptureFailedEventArgs eventArguments) =>
        ScheduleCameraRecovery(eventArguments.Exception);

    private void HudCapture_Failed(
        object? sender,
        CaptureFailedEventArgs eventArguments) =>
        RaiseFailure(eventArguments.Exception);

    private void CameraProcess_Exited(
        object? sender,
        ScrcpyExitedEventArgs eventArguments) =>
        ScheduleCameraRecovery(
            new InvalidOperationException(
                "ライブカメラの受信が停止しました。"));

    private void HudProcess_Exited(
        object? sender,
        ScrcpyExitedEventArgs eventArguments) =>
        RaiseFailure(
            new InvalidOperationException(
                "Rokid画面の受信が停止しました。"));

    private void ScheduleCameraRecovery(Exception cause)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.CompareExchange(
                ref _cameraRecoveryActive,
                1,
                0) != 0)
        {
            return;
        }

        var cancellationToken =
            _sessionCancellation?.Token ?? CancellationToken.None;
        _cameraRecoveryTask = RecoverCameraAsync(cause, cancellationToken);
    }

    private async Task RecoverCameraAsync(
        Exception cause,
        CancellationToken cancellationToken)
    {
        try
        {
            DisposeCamera();
            StatusChanged?.Invoke(
                "撮影中です… カメラ映像の復帰を待っています");
            var originalCameraForeground =
                await _originalCameraForegroundProbe(cancellationToken)
                    .ConfigureAwait(false);
            if (originalCameraForeground == true)
            {
                EnterOriginalCameraScreenMode();
                await MonitorOriginalCameraAsync(cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            _logger.Log(
                $"ライブカメラ再受信を開始 reason={cause.Message}");
            await RestoreLiveCameraAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            RaiseFailure(exception);
        }
        finally
        {
            Volatile.Write(ref _cameraRecoveryActive, 0);
        }
    }

    private void EnterOriginalCameraScreenMode()
    {
        lock (_frameLock)
        {
            _isShowingOriginalCameraScreen = true;
            _cameraFrame = null;
            _frameVersion++;
        }

        _logger.Log("純正カメラ画面へ切替");
        StatusChanged?.Invoke("純正カメラへ切り替えています…");
        DisplaySourceChanged?.Invoke(
            LiveDisplaySource.OriginalCameraScreen);
        ScheduleComposition();
        StatusChanged?.Invoke(null);
    }

    private async Task MonitorOriginalCameraAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            var originalCameraForeground =
                await _originalCameraForegroundProbe(cancellationToken)
                    .ConfigureAwait(false);
            if (originalCameraForeground is null or true)
            {
                continue;
            }

            _logger.Log("純正カメラ終了を検出");
            StatusChanged?.Invoke("ライブ映像へ戻しています…");
            await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken)
                .ConfigureAwait(false);
            await RestoreLiveCameraAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }
    }

    private async Task RestoreLiveCameraAsync(
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 15; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                DisposeCamera();
                var process = new ScrcpyProcessManager(
                    _resources,
                    _logger);
                _cameraProcess = process;
                process.StartLiveCamera(_serial, _width, _height);
                var capture = await StartCaptureAsync(
                    process,
                    CameraCapture_FrameArrived,
                    cancellationToken).ConfigureAwait(false);
                _cameraCapture = capture;
                capture.Failed += CameraCapture_Failed;
                process.Exited += CameraProcess_Exited;

                lock (_frameLock)
                {
                    _isShowingOriginalCameraScreen = false;
                    _frameVersion++;
                }

                _logger.Log($"ライブカメラ再受信成功 attempt={attempt}");
                DisplaySourceChanged?.Invoke(
                    LiveDisplaySource.CameraWithHud);
                StatusChanged?.Invoke(null);
                ScheduleComposition();
                return;
            }
            catch (Exception exception) when (attempt < 15)
            {
                lastError = exception;
                _logger.Log(
                    $"ライブカメラ再受信待機 attempt={attempt} " +
                    exception.Message);
                DisposeCamera();
                StatusChanged?.Invoke(
                    "撮影中です… カメラ映像の復帰を待っています");
            }
        }

        throw lastError ?? new InvalidOperationException(
            "カメラ映像を復旧できませんでした。純正カメラやカメラを使用しているアプリを終了してから、もう一度お試しください。");
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
            _cameraCapture.Failed -= CameraCapture_Failed;
            _cameraCapture.Dispose();
            _cameraCapture = null;
        }

        if (_cameraProcess is not null)
        {
            _cameraProcess.Exited -= CameraProcess_Exited;
            _cameraProcess.Dispose();
            _cameraProcess = null;
        }
    }

    private void DisposeHud()
    {
        if (_hudCapture is not null)
        {
            _hudCapture.FrameArrived -= HudCapture_FrameArrived;
            _hudCapture.Failed -= HudCapture_Failed;
            _hudCapture.Dispose();
            _hudCapture = null;
        }

        if (_hudProcess is not null)
        {
            _hudProcess.Exited -= HudProcess_Exited;
            _hudProcess.Dispose();
            _hudProcess = null;
        }
    }
}
