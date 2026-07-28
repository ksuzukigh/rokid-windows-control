using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RokidControl.App.Services;
using RokidControl.Core.Connections;
using RokidControl.Core.Imaging;
using RokidControl.Core.Navigation;
using RokidControl.Core.Processes;

namespace RokidControl.App;

public partial class MainWindow : Window
{
    private readonly AppLogger _logger;
    private CancellationTokenSource? _operationCancellation;
    private RokidConnectionManager? _connection;
    private ScrcpyProcessManager? _scrcpy;
    private LiveSessionController? _liveSession;
    private WindowsKeyboardController? _keyboard;
    private StandardNavigationOverlay? _standardNavigationOverlay;
    private WriteableBitmap? _liveBitmap;
    private DisplayMode? _selectedMode;
    private bool _isClosing;
    private bool _shutdownCompleted;
    private bool _isRecovering;
    private bool _preferencesLoaded;
    private int _liveScreenWidth;
    private int _liveScreenHeight;
    private LowerNavigationItem? _selectedNavigationItem;

    public MainWindow()
    {
        InitializeComponent();
        _logger = new AppLogger(AppPaths.LogFile);
        LiveVisibilitySlider.Value = AppPreferences.LoadLiveVisibility();
        _preferencesLoaded = true;
        _logger.Log("=== Rokid Control started ===");
    }

    private async void StandardButton_Click(object sender, RoutedEventArgs e)
    {
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

        _selectedMode = DisplayMode.Live;
        await StartSelectedModeAsync();
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
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
            StartStandardSession(
                resources,
                serial,
                screenSize.Width,
                screenSize.Height);
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

    private void StartStandardSession(
        AppResources resources,
        string serial,
        int screenWidth,
        int screenHeight)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("接続管理を開始できませんでした。");
        }

        var scrcpy = new ScrcpyProcessManager(resources, _logger);
        var keyboard = new WindowsKeyboardController(
                _connection,
                _logger,
                screenWidth,
                screenHeight);
        StandardNavigationOverlay? navigationOverlay = null;
        try
        {
            scrcpy.Exited += Scrcpy_Exited;
            scrcpy.StartStandard(serial);
            keyboard.QuitRequested += Keyboard_QuitRequested;
            keyboard.NavigationSelectionChanged +=
                Keyboard_NavigationSelectionChanged;
            navigationOverlay = new StandardNavigationOverlay(
                scrcpy.ProcessId,
                screenWidth,
                screenHeight);
            keyboard.Start(scrcpy.ProcessId);
            _scrcpy = scrcpy;
            _keyboard = keyboard;
            _standardNavigationOverlay = navigationOverlay;
            Hide();
        }
        catch
        {
            keyboard.QuitRequested -= Keyboard_QuitRequested;
            keyboard.NavigationSelectionChanged -=
                Keyboard_NavigationSelectionChanged;
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
            screenHeight);
        var keyboard = new WindowsKeyboardController(
            _connection,
            _logger,
            screenWidth,
            screenHeight);
        try
        {
            liveSession.Visibility = LiveVisibilitySlider.Value;
            liveSession.FrameReady += LiveSession_FrameReady;
            liveSession.Failed += LiveSession_Failed;
            keyboard.QuitRequested += Keyboard_QuitRequested;
            keyboard.NavigationSelectionChanged +=
                Keyboard_NavigationSelectionChanged;
            await liveSession.StartAsync(
                serial,
                new Progress<string>(ShowProgress),
                cancellationToken);
            keyboard.Start(Environment.ProcessId);
            _liveScreenWidth = screenWidth;
            _liveScreenHeight = screenHeight;
            _selectedNavigationItem = null;
            _liveSession = liveSession;
            _keyboard = keyboard;
            ShowLivePanel();
        }
        catch
        {
            keyboard.QuitRequested -= Keyboard_QuitRequested;
            keyboard.NavigationSelectionChanged -=
                Keyboard_NavigationSelectionChanged;
            keyboard.Dispose();
            liveSession.FrameReady -= LiveSession_FrameReady;
            liveSession.Failed -= LiveSession_Failed;
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

    private void Scrcpy_Exited(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _ = RecoverStandardSessionAsync();
        });
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
            StartStandardSession(
                resources,
                serial,
                screenSize.Width,
                screenSize.Height);
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

    private void LiveSession_FrameReady(
        object? sender,
        LiveFrameEventArgs eventArguments)
    {
        Dispatcher.InvokeAsync(() => DisplayLiveFrame(eventArguments.Frame));
    }

    private void LiveSession_Failed(Exception exception)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _ = RecoverLiveSessionAsync(exception);
        });
    }

    private async Task RecoverLiveSessionAsync(Exception? cause = null)
    {
        if (_isClosing || _isRecovering || _connection is null)
        {
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
        UpdateNavigationSelectionRing();
    }

    private void ShowLivePanel()
    {
        Dispatcher.VerifyAccess();
        ModePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LivePanel.Visibility = Visibility.Visible;
        Title = "Rokid AI Glasses RV101（ライブ映像）";
        MinWidth = 360;
        MinHeight = 480;
        Width = 480;
        Height = 720;
        Show();
        Activate();
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

    private void Keyboard_NavigationSelectionChanged(
        LowerNavigationItem? selectedItem)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _selectedNavigationItem = selectedItem;
            UpdateNavigationSelectionRing();
            _standardNavigationOverlay?.SetSelection(selectedItem);
        });
    }

    private void LiveImage_SizeChanged(
        object sender,
        SizeChangedEventArgs eventArguments)
    {
        UpdateNavigationSelectionRing();
    }

    private void UpdateNavigationSelectionRing()
    {
        Dispatcher.VerifyAccess();
        if (_selectedNavigationItem is null ||
            _liveBitmap is null ||
            _liveScreenWidth <= 0 ||
            _liveScreenHeight <= 0 ||
            LiveImage.ActualWidth <= 0 ||
            LiveImage.ActualHeight <= 0)
        {
            NavigationSelectionOuter.Visibility = Visibility.Collapsed;
            NavigationSelectionRing.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = Math.Min(
            LiveImage.ActualWidth / _liveBitmap.PixelWidth,
            LiveImage.ActualHeight / _liveBitmap.PixelHeight);
        var displayedWidth = _liveBitmap.PixelWidth * scale;
        var displayedHeight = _liveBitmap.PixelHeight * scale;
        var offsetX = (LiveImage.ActualWidth - displayedWidth) / 2;
        var offsetY = (LiveImage.ActualHeight - displayedHeight) / 2;
        var devicePoint = _selectedNavigationItem.Value.GetDevicePoint(
            _liveScreenWidth,
            _liveScreenHeight);
        var bitmapX =
            devicePoint.X * (double)_liveBitmap.PixelWidth / _liveScreenWidth;
        var bitmapY =
            devicePoint.Y * (double)_liveBitmap.PixelHeight / _liveScreenHeight;
        var diameter = Math.Clamp(56 * scale, 26, 70);
        var left = offsetX + bitmapX * scale - diameter / 2;
        var top = offsetY + bitmapY * scale - diameter / 2;

        foreach (var ring in new[]
                 {
                     NavigationSelectionOuter,
                     NavigationSelectionRing,
                 })
        {
            ring.Width = diameter;
            ring.Height = diameter;
            Canvas.SetLeft(ring, left);
            Canvas.SetTop(ring, top);
            ring.Visibility = Visibility.Visible;
        }
    }

    private async void LiveImage_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArguments)
    {
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
        Application.Current.Shutdown();
    }

    private void StopLocalSession()
    {
        if (_keyboard is not null)
        {
            _keyboard.QuitRequested -= Keyboard_QuitRequested;
            _keyboard.NavigationSelectionChanged -=
                Keyboard_NavigationSelectionChanged;
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
            _liveSession.Dispose();
            _liveSession = null;
        }

        _liveBitmap = null;
        _liveScreenWidth = 0;
        _liveScreenHeight = 0;
        _selectedNavigationItem = null;
        LiveImage.Source = null;
        NavigationSelectionOuter.Visibility = Visibility.Collapsed;
        NavigationSelectionRing.Visibility = Visibility.Collapsed;
    }
}

internal enum DisplayMode
{
    Standard,
    Live,
}
