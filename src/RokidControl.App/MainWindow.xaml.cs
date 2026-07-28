using System.ComponentModel;
using System.Windows;
using RokidControl.App.Services;
using RokidControl.Core.Connections;
using RokidControl.Core.Processes;

namespace RokidControl.App;

public partial class MainWindow : Window
{
    private readonly AppLogger _logger;
    private CancellationTokenSource? _operationCancellation;
    private RokidConnectionManager? _connection;
    private ScrcpyProcessManager? _scrcpy;
    private WindowsKeyboardController? _keyboard;
    private DisplayMode? _selectedMode;
    private bool _isClosing;
    private bool _shutdownCompleted;
    private bool _isRecovering;

    public MainWindow()
    {
        InitializeComponent();
        _logger = new AppLogger(AppPaths.LogFile);
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
                $"接続成功 serial={serial} size={screenSize.Width}x{screenSize.Height}");

            if (_selectedMode == DisplayMode.Live)
            {
                throw new NotSupportedException(
                    "ライブ映像のWindows Graphics Capture試験は実装中です。背景なし（省電力）は先に実機試験できます。");
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
        try
        {
            scrcpy.Exited += Scrcpy_Exited;
            scrcpy.StartStandard(serial);
            keyboard.QuitRequested += Keyboard_QuitRequested;
            keyboard.Start(scrcpy.ProcessId);
            _scrcpy = scrcpy;
            _keyboard = keyboard;
            Hide();
        }
        catch
        {
            keyboard.QuitRequested -= Keyboard_QuitRequested;
            keyboard.Dispose();
            scrcpy.Exited -= Scrcpy_Exited;
            scrcpy.Dispose();
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
            _logger.Log($"自動再接続成功 serial={serial}");
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

    private void ShowProgress(string message)
    {
        Dispatcher.VerifyAccess();
        ModePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private void ShowError(string message)
    {
        Show();
        Activate();
        ModePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
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
            _keyboard.Dispose();
            _keyboard = null;
        }

        if (_scrcpy is not null)
        {
            _scrcpy.Exited -= Scrcpy_Exited;
            _scrcpy.Dispose();
            _scrcpy = null;
        }
    }
}

internal enum DisplayMode
{
    Standard,
    Live,
}
