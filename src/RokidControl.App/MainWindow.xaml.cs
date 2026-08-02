using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RokidControl.App.Services;
using RokidControl.Core.Connections;
using RokidControl.Core.Imaging;
using RokidControl.Core.Navigation;
using RokidControl.Core.Processes;

namespace RokidControl.App;

public partial class MainWindow : Window
{
    private readonly AppLogger _logger;
    private readonly RapidFailureGuard _standardFailureGuard =
        new(2, TimeSpan.FromSeconds(5));
    private readonly RapidFailureGuard _liveFailureGuard =
        new(2, TimeSpan.FromSeconds(5));
    private readonly DispatcherTimer _focusRestoringPointerTimer;
    private readonly FocusRestoringPointerState _focusRestoringPointerState =
        new();
    private CancellationTokenSource? _operationCancellation;
    private RokidConnectionManager? _connection;
    private ScrcpyProcessManager? _scrcpy;
    private LiveSessionController? _liveSession;
    private WindowsKeyboardController? _keyboard;
    private StandardNavigationOverlay? _standardNavigationOverlay;
    private WriteableBitmap? _liveBitmap;
    private BgraFrame? _pendingLiveFrame;
    private DisplayMode? _selectedMode;
    private bool _isClosing;
    private bool _shutdownCompleted;
    private bool _isRecovering;
    private bool _preferencesLoaded;
    private int _liveFrameDispatchScheduled;

    public MainWindow()
    {
        InitializeComponent();
        _focusRestoringPointerTimer = new DispatcherTimer(
            DispatcherPriority.Input)
        {
            Interval = PointerSelectionPolicy.FocusRestoringClickWindow,
        };
        _focusRestoringPointerTimer.Tick +=
            FocusRestoringPointerTimer_Tick;
        _logger = new AppLogger(AppPaths.LogFile);
        LiveVisibilitySlider.Value = AppPreferences.LoadLiveVisibility();
        _preferencesLoaded = true;
        _logger.Log("=== Rokid Control started ===");
    }

    private void Window_Loaded(object sender, RoutedEventArgs eventArguments)
    {
        FitInitialWindowToCurrentWorkArea(560, 600, 440, 480);
    }

    private void Window_Activated(object? sender, EventArgs eventArguments)
    {
        if (_focusRestoringPointerState.IsPending)
        {
            _focusRestoringPointerTimer.Stop();
            _focusRestoringPointerTimer.Start();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs eventArguments)
    {
        _focusRestoringPointerTimer.Stop();
        _focusRestoringPointerState.MarkWindowDeactivated();
    }

    private void FocusRestoringPointerTimer_Tick(
        object? sender,
        EventArgs eventArguments)
    {
        _focusRestoringPointerTimer.Stop();
        _focusRestoringPointerState.Expire();
    }

    private async void StandardButton_Click(object sender, RoutedEventArgs e)
    {
        _standardFailureGuard.Reset();
        _selectedMode = DisplayMode.Standard;
        await StartSelectedModeAsync();
    }

    private async void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        var response = MessageBox.Show(
            "ライブ映像はRokidのカメラを使用するため、背景なしより電池を多く消費します。開始しますか？",
            "ライブ映像を開始します",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (response != MessageBoxResult.OK)
        {
            return;
        }

        _liveFailureGuard.Reset();
        _selectedMode = DisplayMode.Live;
        await StartSelectedModeAsync();
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMode == DisplayMode.Standard)
        {
            _standardFailureGuard.Reset();
        }
        else if (_selectedMode == DisplayMode.Live)
        {
            _liveFailureGuard.Reset();
        }

        if (_selectedMode == DisplayMode.Standard && _connection is not null)
        {
            await RecoverStandardSessionAsync();
        }
        else if (_selectedMode == DisplayMode.Live && _connection is not null)
        {
            await RecoverLiveSessionAsync();
        }
        else if (_selectedMode is not null)
        {
            await StartSelectedModeAsync();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        Close();
    }

    private async Task StartSelectedModeAsync()
    {
        if (_selectedMode is null)
        {
            return;
        }

        ShowProgress("Rokidを探しています…");
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;

        try
        {
            var resources = AppResources.Locate();
            _logger.Log($"表示モード {_selectedMode}");
            var runner = new ProcessRunner(resources.CreateEnvironment());
            var adb = new AdbClient(resources.AdbPath, runner);
            _connection = new RokidConnectionManager(
                adb,
                AppPaths.WifiAddressFile,
                resources.WatchdogPath);

            await _connection.PrepareAdbServerAsync(cancellationToken);
            var progress = new Progress<string>(ShowProgress);
            var serial = await _connection.ConnectForStartupAsync(
                progress,
                cancellationToken: cancellationToken);
            ShowProgress("Windows操作モードを開始しています…");
            await StartWindowsModeWithRetryAsync(cancellationToken);
            var screenSize = await _connection.GetScreenSizeAsync(cancellationToken);
            _logger.Log(
                $"接続成功 size={screenSize.Width}x{screenSize.Height}");

            if (_selectedMode == DisplayMode.Live)
            {
                ShowProgress("ライブ映像を準備しています…");
                await StartLiveSessionAsync(
                    resources,
                    serial,
                    screenSize.Width,
                    screenSize.Height,
                    cancellationToken);
                return;
            }

            ShowProgress("画面を受信しています…");
            await StartStandardSessionAsync(
                resources,
                serial,
                screenSize.Width,
                screenSize.Height,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                Close();
            }
        }
        catch (Exception exception)
        {
            _logger.Log($"ERROR {exception}");
            ShowError(exception.Message);
        }
    }

    private async Task StartStandardSessionAsync(
        AppResources resources,
        string serial,
        int screenWidth,
        int screenHeight,
        CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("接続管理を開始できませんでした。");
        }

        var scrcpy = new ScrcpyProcessManager(resources, _logger);
        var input = new PersistentAdbInputSession(
            resources.AdbPath,
            serial,
            resources.CreateEnvironment(),
            _logger.Log);
        await input.VerifyReadyAsync(cancellationToken);
        var keyboard = new WindowsKeyboardController(
            input,
            _logger,
            screenWidth,
            screenHeight);
        StandardNavigationOverlay? navigationOverlay = null;
        try
        {
            scrcpy.Exited += Scrcpy_Exited;
            scrcpy.StartStandard(serial);
            keyboard.QuitRequested += Keyboard_QuitRequested;
            keyboard.ApplicationMenuModeChanged +=
                Keyboard_ApplicationMenuModeChanged;
            navigationOverlay = new StandardNavigationOverlay(
                scrcpy.ProcessId);
            keyboard.Start(scrcpy.ProcessId);
            var activated = await scrcpy.ActivateWindowAsync(
                cancellationToken);
            if (!activated)
            {
                _logger.Log(
                    "scrcpyを自動で入力先にできませんでした。画面をクリックしてください。");
            }

            _scrcpy = scrcpy;
            _keyboard = keyboard;
            _standardNavigationOverlay = navigationOverlay;
            Hide();
        }
        catch
        {
            keyboard.QuitRequested -= Keyboard_QuitRequested;
            keyboard.ApplicationMenuModeChanged -=
                Keyboard_ApplicationMenuModeChanged;
            navigationOverlay?.Dispose();
            keyboard.Dispose();
            scrcpy.Exited -= Scrcpy_Exited;
            scrcpy.Dispose();
            throw;
        }
    }

    private async Task StartLiveSessionAsync(
        AppResources resources,
        string serial,
        int screenWidth,
        int screenHeight,
        CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("接続管理を開始できませんでした。");
        }

        var liveSession = new LiveSessionController(
            resources,
            _logger,
            screenWidth,
            screenHeight,
            _connection.IsOriginalCameraForegroundAsync);
        var input = new PersistentAdbInputSession(
            resources.AdbPath,
            serial,
            resources.CreateEnvironment(),
            _logger.Log);
        await input.VerifyReadyAsync(cancellationToken);
        var keyboard = new WindowsKeyboardController(
            input,
            _logger,
            screenWidth,
            screenHeight);
        try
        {
            liveSession.Visibility = LiveVisibilitySlider.Value;
            liveSession.FrameReady += LiveSession_FrameReady;
            liveSession.Failed += LiveSession_Failed;
            liveSession.DisplaySourceChanged +=
                LiveSession_DisplaySourceChanged;
            liveSession.StatusChanged += LiveSession_StatusChanged;
            keyboard.QuitRequested += Keyboard_QuitRequested;
            keyboard.ApplicationMenuModeChanged +=
                Keyboard_ApplicationMenuModeChanged;
            await liveSession.StartAsync(
                serial,
                new Progress<string>(ShowProgress),
                cancellationToken);
            keyboard.Start(Environment.ProcessId);
            _liveSession = liveSession;
            _keyboard = keyboard;
            ShowLivePanel();
        }
        catch
        {
            keyboard.QuitRequested -= Keyboard_QuitRequested;
            keyboard.ApplicationMenuModeChanged -=
                Keyboard_ApplicationMenuModeChanged;
            keyboard.Dispose();
            liveSession.FrameReady -= LiveSession_FrameReady;
            liveSession.Failed -= LiveSession_Failed;
            liveSession.DisplaySourceChanged -=
                LiveSession_DisplaySourceChanged;
            liveSession.StatusChanged -= LiveSession_StatusChanged;
            liveSession.Dispose();
            throw;
        }
    }

    private async Task StartWindowsModeWithRetryAsync(
        CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("接続管理を開始できませんでした。");
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _connection.StartWindowsModeAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < 3)
            {
                lastError = exception;
                _logger.Log($"Windows操作モード開始失敗 attempt={attempt}");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        throw lastError ??
            new RokidConnectionException(RokidConnectionError.WatchdogFailed);
    }

    private void Scrcpy_Exited(
        object? sender,
        ScrcpyExitedEventArgs eventArguments)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _ = HandleStandardSessionExitedAsync(eventArguments.ExitCode);
        });
    }

    private async Task HandleStandardSessionExitedAsync(int exitCode)
    {
        if (_isClosing || _connection is null)
        {
            return;
        }

        var connectionAlive = false;
        try
        {
            connectionAlive = await _connection.IsCurrentConnectionAliveAsync();
        }
        catch (Exception exception)
        {
            _logger.Log(
                $"scrcpy終了時の接続確認失敗 {exception.Message}");
        }

        var action = StandardSessionExitPolicy.Decide(
            exitCode,
            connectionAlive);
        if (action == StandardSessionExitAction.Quit)
        {
            _logger.Log("背景なし画面が閉じられたためアプリを終了");
            Close();
            return;
        }

        if (_standardFailureGuard.RecordFailure(DateTimeOffset.UtcNow))
        {
            _logger.Log("scrcpyの短時間終了が続いたため自動再接続を停止");
            StopLocalSession();
            ShowError(
                "画面の接続が短時間に繰り返し終了しました。通信状態を確認して「再接続」を押すか、「終了」を押してください。");
            return;
        }

        await RecoverStandardSessionAsync();
    }

    private async Task RecoverStandardSessionAsync()
    {
        if (_isClosing || _isRecovering || _connection is null)
        {
            return;
        }

        _isRecovering = true;
        StopLocalSession();
        Show();
        ShowProgress("Rokidへの接続を復旧しています…");
        _logger.Log("scrcpy終了後の自動再接続を開始");

        try
        {
            var cancellationToken = _operationCancellation?.Token ??
                CancellationToken.None;
            var serial = await _connection.ReconnectAsync(cancellationToken);
            if (serial is null)
            {
                throw new RokidConnectionException(
                    RokidConnectionError.NoDevice);
            }

            await StartWindowsModeWithRetryAsync(cancellationToken);
            var screenSize = await _connection.GetScreenSizeAsync(
                cancellationToken);
            var resources = AppResources.Locate();
            await StartStandardSessionAsync(
                resources,
                serial,
                screenSize.Width,
                screenSize.Height,
                cancellationToken);
            _logger.Log("自動再接続成功");
        }
        catch (OperationCanceledException) when (_isClosing)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.Log($"自動再接続失敗 {exception}");
            ShowError(exception.Message);
        }
        finally
        {
            _isRecovering = false;
        }
    }

    private void Keyboard_QuitRequested()
    {
        Dispatcher.InvokeAsync(Close);
    }

    internal void ActivateCurrentSession()
    {
        Dispatcher.VerifyAccess();
        if (_isClosing)
        {
            return;
        }

        if (_scrcpy is not null)
        {
            _ = ActivateStandardSessionAsync();
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async Task ActivateStandardSessionAsync()
    {
        try
        {
            if (_scrcpy is not null &&
                !await _scrcpy.ActivateWindowAsync(CancellationToken.None))
            {
                _logger.Log("既存の背景なし画面を前面にできませんでした。");
            }
        }
        catch (Exception exception)
        {
            _logger.Log(
                $"既存の背景なし画面を前面にできませんでした: {exception.Message}");
        }
    }

    private void LiveSession_FrameReady(
        object? sender,
        LiveFrameEventArgs eventArguments)
    {
        Interlocked.Exchange(ref _pendingLiveFrame, eventArguments.Frame);
        ScheduleLiveFrameDisplay();
    }

    private void LiveSession_Failed(Exception exception)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _ = RecoverLiveSessionAsync(exception);
        });
    }

    private void LiveSession_DisplaySourceChanged(LiveDisplaySource source)
    {
        Dispatcher.InvokeAsync(() =>
        {
            Title = source == LiveDisplaySource.OriginalCameraScreen
                ? "Rokid AI Glasses RV101（純正カメラ）"
                : "Rokid AI Glasses RV101（ライブ映像）";
        });
    }

    private void LiveSession_StatusChanged(string? message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LiveStatusText.Text = message ?? string.Empty;
            LiveStatus.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        });
    }

    private async Task RecoverLiveSessionAsync(Exception? cause = null)
    {
        if (_isClosing || _isRecovering || _connection is null)
        {
            return;
        }

        if (cause is not null &&
            _liveFailureGuard.RecordFailure(DateTimeOffset.UtcNow))
        {
            _logger.Log("ライブ映像の短時間終了が続いたため自動再接続を停止");
            StopLocalSession();
            ShowError(
                "ライブ映像の接続が短時間に繰り返し終了しました。通信状態を確認して「再接続」を押すか、「終了」を押してください。");
            return;
        }

        _isRecovering = true;
        if (cause is not null)
        {
            _logger.Log($"Live session stopped unexpectedly: {cause}");
        }

        StopLocalSession();
        Show();
        ShowProgress("ライブ映像を再接続しています…");

        try
        {
            var cancellationToken = _operationCancellation?.Token ??
                CancellationToken.None;
            var serial = await _connection.ReconnectAsync(cancellationToken);
            if (serial is null)
            {
                throw new RokidConnectionException(
                    RokidConnectionError.NoDevice);
            }

            await StartWindowsModeWithRetryAsync(cancellationToken);
            var screenSize = await _connection.GetScreenSizeAsync(
                cancellationToken);
            var resources = AppResources.Locate();
            await StartLiveSessionAsync(
                resources,
                serial,
                screenSize.Width,
                screenSize.Height,
                cancellationToken);
            _logger.Log("Live session reconnected successfully.");
        }
        catch (OperationCanceledException) when (_isClosing)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.Log($"Live session reconnection failed: {exception}");
            ShowError(exception.Message);
        }
        finally
        {
            _isRecovering = false;
        }
    }

    private void DisplayLiveFrame(BgraFrame frame)
    {
        Dispatcher.VerifyAccess();
        if (_liveBitmap is null ||
            _liveBitmap.PixelWidth != frame.Width ||
            _liveBitmap.PixelHeight != frame.Height)
        {
            _liveBitmap = new WriteableBitmap(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null);
            LiveImage.Source = _liveBitmap;
        }

        _liveBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Pixels,
            frame.Stride,
            0);
    }

    private void ScheduleLiveFrameDisplay()
    {
        if (Interlocked.CompareExchange(
                ref _liveFrameDispatchScheduled,
                1,
                0) != 0)
        {
            return;
        }

        Dispatcher.InvokeAsync(DisplayLatestLiveFrame);
    }

    private void DisplayLatestLiveFrame()
    {
        Dispatcher.VerifyAccess();
        var frame = Interlocked.Exchange(ref _pendingLiveFrame, null);
        if (frame is not null && !_isClosing && _liveSession is not null)
        {
            DisplayLiveFrame(frame);
        }

        Volatile.Write(ref _liveFrameDispatchScheduled, 0);
        if (Volatile.Read(ref _pendingLiveFrame) is not null)
        {
            ScheduleLiveFrameDisplay();
        }
    }

    private void ShowLivePanel()
    {
        Dispatcher.VerifyAccess();
        ModePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LivePanel.Visibility = Visibility.Visible;
        KeyboardHint.Visibility = Visibility.Visible;
        LiveStatus.Visibility = Visibility.Collapsed;
        KeyboardHintText.Text = NavigationHintText.Normal;
        Title = "Rokid AI Glasses RV101（ライブ映像）";
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Show();
        Activate();
    }

    private void FitInitialWindowToCurrentWorkArea(
        double preferredWidth,
        double preferredHeight,
        double preferredMinWidth,
        double preferredMinHeight)
    {
        var workArea = GetCurrentMonitorWorkArea();
        var availableWidth = Math.Max(320, workArea.Width - 32);
        var availableHeight = Math.Max(360, workArea.Height - 32);
        var targetWidth = Math.Min(preferredWidth, availableWidth);
        var targetHeight = Math.Min(preferredHeight, availableHeight);

        MinWidth = Math.Min(preferredMinWidth, targetWidth);
        MinHeight = Math.Min(preferredMinHeight, targetHeight);
        Width = targetWidth;
        Height = targetHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp(
            Left,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - targetWidth));
        Top = Math.Clamp(
            Top,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - targetHeight));
    }

    private Rect GetCurrentMonitorWorkArea()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var monitor = NativeMethods.MonitorFromWindow(
            windowHandle,
            NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        if (monitor == IntPtr.Zero ||
            !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return SystemParameters.WorkArea;
        }

        var dpi = NativeMethods.GetDpiForWindow(windowHandle);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        return new Rect(
            monitorInfo.WorkArea.Left / scale,
            monitorInfo.WorkArea.Top / scale,
            (monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left) / scale,
            (monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top) / scale);
    }

    private void LiveVisibilitySlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArguments)
    {
        if (_liveSession is not null)
        {
            _liveSession.Visibility = eventArguments.NewValue;
        }

        if (_preferencesLoaded)
        {
            try
            {
                AppPreferences.SaveLiveVisibility(eventArguments.NewValue);
            }
            catch (Exception exception)
            {
                _logger.Log(
                    $"Live visibility setting could not be saved: {exception.Message}");
            }
        }
    }

    private void Keyboard_ApplicationMenuModeChanged(bool active)
    {
        Dispatcher.InvokeAsync(() =>
        {
            KeyboardHintText.Text = active
                ? NavigationHintText.ApplicationMenu
                : NavigationHintText.Normal;
            _standardNavigationOverlay?.SetApplicationMenuActive(active);
        });
    }

    private async void LiveImage_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArguments)
    {
        if (PointerInputRestoredWindowFocus())
        {
            return;
        }

        _keyboard?.EndApplicationMenuSelection();
        if (_connection is null ||
            !TryMapLivePoint(
                eventArguments.GetPosition(LiveImage),
                out var x,
                out var y))
        {
            return;
        }

        try
        {
            await _connection.TapAsync(
                x,
                y,
                _operationCancellation?.Token ?? CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.Log($"ライブ映像タップ失敗 {exception.Message}");
        }
    }

    private async void LiveImage_MouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArguments)
    {
        if (PointerInputRestoredWindowFocus())
        {
            return;
        }

        _keyboard?.EndApplicationMenuSelection();
        if (_connection is null)
        {
            return;
        }

        try
        {
            await _connection.SendKeyEventAsync(
                "KEYCODE_BACK",
                _operationCancellation?.Token ?? CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.Log($"ライブ映像戻る操作失敗 {exception.Message}");
        }
    }

    private bool TryMapLivePoint(Point point, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (_liveBitmap is null ||
            LiveImage.ActualWidth <= 0 ||
            LiveImage.ActualHeight <= 0)
        {
            return false;
        }

        var scale = Math.Min(
            LiveImage.ActualWidth / _liveBitmap.PixelWidth,
            LiveImage.ActualHeight / _liveBitmap.PixelHeight);
        var displayedWidth = _liveBitmap.PixelWidth * scale;
        var displayedHeight = _liveBitmap.PixelHeight * scale;
        var offsetX = (LiveImage.ActualWidth - displayedWidth) / 2;
        var offsetY = (LiveImage.ActualHeight - displayedHeight) / 2;
        if (point.X < offsetX ||
            point.Y < offsetY ||
            point.X >= offsetX + displayedWidth ||
            point.Y >= offsetY + displayedHeight)
        {
            return false;
        }

        x = Math.Clamp(
            (int)((point.X - offsetX) / scale),
            0,
            _liveBitmap.PixelWidth - 1);
        y = Math.Clamp(
            (int)((point.Y - offsetY) / scale),
            0,
            _liveBitmap.PixelHeight - 1);
        return true;
    }

    private bool PointerInputRestoredWindowFocus()
    {
        if (!_focusRestoringPointerState.TryConsume())
        {
            return false;
        }

        _focusRestoringPointerTimer.Stop();
        return true;
    }

    private void ShowProgress(string message)
    {
        Dispatcher.VerifyAccess();
        ModePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LivePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private void ShowError(string message)
    {
        Show();
        Activate();
        ModePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        LivePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _focusRestoringPointerTimer.Stop();
        _focusRestoringPointerTimer.Tick -=
            FocusRestoringPointerTimer_Tick;
        using var shutdownDisplayCancellation =
            new CancellationTokenSource();
        var shutdownDisplayTask = ShowShutdownIfSlowAsync(
            shutdownDisplayCancellation.Token);
        _operationCancellation?.Cancel();
        StopLocalSession();

        if (_connection is not null)
        {
            using var shutdownTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(4));
            try
            {
                await _connection.StopWindowsModeAsync(shutdownTimeout.Token);
            }
            catch (Exception exception)
            {
                _logger.Log($"終了処理エラー {exception.Message}");
            }

            await _connection.DisposeAsync();
            _connection = null;
        }

        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _logger.Log("Rokid Control終了");
        _logger.Dispose();
        _shutdownCompleted = true;
        shutdownDisplayCancellation.Cancel();
        try
        {
            await shutdownDisplayTask;
        }
        catch (OperationCanceledException)
        {
            // Shutdown completed before the delayed status was needed.
        }

        Application.Current.Shutdown();
    }

    private async Task ShowShutdownIfSlowAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(
            TimeSpan.FromMilliseconds(400),
            cancellationToken);
        if (!_shutdownCompleted)
        {
            Show();
            ShowProgress("終了しています…");
            ProgressCancelButton.IsEnabled = false;
        }
    }

    private void StopLocalSession()
    {
        if (_keyboard is not null)
        {
            _keyboard.QuitRequested -= Keyboard_QuitRequested;
            _keyboard.ApplicationMenuModeChanged -=
                Keyboard_ApplicationMenuModeChanged;
            _keyboard.Dispose();
            _keyboard = null;
        }

        _standardNavigationOverlay?.Dispose();
        _standardNavigationOverlay = null;

        if (_scrcpy is not null)
        {
            _scrcpy.Exited -= Scrcpy_Exited;
            _scrcpy.Dispose();
            _scrcpy = null;
        }

        if (_liveSession is not null)
        {
            _liveSession.FrameReady -= LiveSession_FrameReady;
            _liveSession.Failed -= LiveSession_Failed;
            _liveSession.DisplaySourceChanged -=
                LiveSession_DisplaySourceChanged;
            _liveSession.StatusChanged -= LiveSession_StatusChanged;
            _liveSession.Dispose();
            _liveSession = null;
        }

        _liveBitmap = null;
        Interlocked.Exchange(ref _pendingLiveFrame, null);
        LiveImage.Source = null;
        KeyboardHint.Visibility = Visibility.Collapsed;
        LiveStatus.Visibility = Visibility.Collapsed;
    }

    private static class NativeMethods
    {
        internal const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            internal uint Size;
            internal NativeRect MonitorArea;
            internal NativeRect WorkArea;
            internal uint Flags;
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(
            IntPtr windowHandle,
            uint flags);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(
            IntPtr monitor,
            ref MonitorInfo monitorInfo);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr windowHandle);
    }
}

internal enum DisplayMode
{
    Standard,
    Live,
}
