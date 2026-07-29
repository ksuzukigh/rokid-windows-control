using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RokidControl.Core.Navigation;

namespace RokidControl.App.Services;

internal sealed class StandardNavigationOverlay : IDisposable
{
    private const double DeviceRingSize = 44;
    private const double DeviceOuterInset = 3;
    private const double DeviceOuterStroke = 7;
    private const double DeviceInnerInset = 5;
    private const double DeviceInnerStroke = 3;
    private readonly int _processId;
    private readonly int _screenWidth;
    private readonly int _screenHeight;
    private readonly Window _window;
    private readonly Ellipse _outer;
    private readonly Ellipse _inner;
    private readonly DispatcherTimer _trackingTimer;
    private LowerNavigationItem? _selectedItem;
    private bool _disposed;

    public StandardNavigationOverlay(
        int processId,
        int screenWidth,
        int screenHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenHeight);

        _processId = processId;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _window = CreateWindow(out _outer, out _inner);
        _window.SourceInitialized += Window_SourceInitialized;

        _trackingTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Background,
            TrackingTimer_Tick,
            _window.Dispatcher);
        _trackingTimer.Start();
    }

    public void SetSelection(LowerNavigationItem? selectedItem)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _selectedItem = selectedItem;
        UpdatePosition();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trackingTimer.Stop();
        _window.SourceInitialized -= Window_SourceInitialized;
        _window.Close();
    }

    private static Window CreateWindow(
        out Ellipse outer,
        out Ellipse inner)
    {
        outer = new Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromArgb(166, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        inner = new Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromRgb(100, 210, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new Grid
        {
            IsHitTestVisible = false,
        };
        content.Children.Add(outer);
        content.Children.Add(inner);

        return new Window
        {
            Width = DeviceRingSize,
            Height = DeviceRingSize,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            IsHitTestVisible = false,
            Content = content,
            Visibility = Visibility.Hidden,
        };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        const int extendedStyleIndex = -20;
        const nint transparentStyle = 0x00000020;
        const nint toolWindowStyle = 0x00000080;
        const nint noActivateStyle = 0x08000000;

        var handle = new WindowInteropHelper(_window).Handle;
        var style = NativeMethods.GetWindowLongPointer(
            handle,
            extendedStyleIndex);
        _ = NativeMethods.SetWindowLongPointer(
            handle,
            extendedStyleIndex,
            style | transparentStyle | toolWindowStyle | noActivateStyle);
    }

    private void TrackingTimer_Tick(object? sender, EventArgs e)
    {
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_disposed || _selectedItem is null)
        {
            _window.Hide();
            return;
        }

        try
        {
            using var process = Process.GetProcessById(_processId);
            process.Refresh();
            var targetWindow = process.MainWindowHandle;
            if (process.HasExited ||
                targetWindow == nint.Zero ||
                NativeMethods.GetForegroundWindow() != targetWindow ||
                !NativeMethods.GetClientRect(targetWindow, out var clientRect))
            {
                _window.Hide();
                return;
            }

            var clientOrigin = new NativeMethods.NativePoint();
            if (!NativeMethods.ClientToScreen(targetWindow, ref clientOrigin))
            {
                _window.Hide();
                return;
            }

            var clientWidth = clientRect.Right - clientRect.Left;
            var clientHeight = clientRect.Bottom - clientRect.Top;
            if (clientWidth <= 0 || clientHeight <= 0)
            {
                _window.Hide();
                return;
            }

            var scale = Math.Min(
                clientWidth / (double)_screenWidth,
                clientHeight / (double)_screenHeight);
            var displayedWidth = _screenWidth * scale;
            var displayedHeight = _screenHeight * scale;
            var contentLeft =
                clientOrigin.X + (clientWidth - displayedWidth) / 2;
            var contentTop =
                clientOrigin.Y + (clientHeight - displayedHeight) / 2;
            var devicePoint = _selectedItem.Value.GetHighlightPoint(
                _screenWidth,
                _screenHeight);
            var centerX = contentLeft + devicePoint.X * scale;
            var centerY = contentTop + devicePoint.Y * scale;
            var dpi = Math.Max(NativeMethods.GetDpiForWindow(targetWindow), 96);
            var pixelsPerDip = dpi / 96d;
            var ringSize = DeviceRingSize * scale / pixelsPerDip;
            var outerInset = DeviceOuterInset * scale / pixelsPerDip;
            var innerInset = DeviceInnerInset * scale / pixelsPerDip;

            _window.Width = ringSize;
            _window.Height = ringSize;
            _outer.Width = ringSize - 2 * outerInset;
            _outer.Height = ringSize - 2 * outerInset;
            _outer.StrokeThickness =
                DeviceOuterStroke * scale / pixelsPerDip;
            _inner.Width = ringSize - 2 * innerInset;
            _inner.Height = ringSize - 2 * innerInset;
            _inner.StrokeThickness =
                DeviceInnerStroke * scale / pixelsPerDip;
            _window.Left = centerX / pixelsPerDip - ringSize / 2d;
            _window.Top = centerY / pixelsPerDip - ringSize / 2d;
            if (!_window.IsVisible)
            {
                _window.Show();
            }
        }
        catch (ArgumentException)
        {
            _window.Hide();
        }
        catch (InvalidOperationException)
        {
            _window.Hide();
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        internal static nint GetWindowLongPointer(nint window, int index) =>
            Environment.Is64BitProcess
                ? GetWindowLongPtr64(window, index)
                : GetWindowLong32(window, index);

        internal static nint SetWindowLongPointer(
            nint window,
            int index,
            nint value) =>
            Environment.Is64BitProcess
                ? SetWindowLongPtr64(window, index, value)
                : SetWindowLong32(window, index, value);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowLongPtrW",
            SetLastError = true)]
        private static extern nint GetWindowLongPtr64(nint window, int index);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowLongW",
            SetLastError = true)]
        private static extern nint GetWindowLong32(nint window, int index);

        [DllImport(
            "user32.dll",
            EntryPoint = "SetWindowLongPtrW",
            SetLastError = true)]
        private static extern nint SetWindowLongPtr64(
            nint window,
            int index,
            nint value);

        [DllImport(
            "user32.dll",
            EntryPoint = "SetWindowLongW",
            SetLastError = true)]
        private static extern nint SetWindowLong32(
            nint window,
            int index,
            nint value);

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(
            nint window,
            out NativeRect rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(
            nint window,
            ref NativePoint point);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);
    }
}
