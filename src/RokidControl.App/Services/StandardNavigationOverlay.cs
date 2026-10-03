using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RokidControl.Core.Navigation;

namespace RokidControl.App.Services;

internal sealed class StandardNavigationOverlay : IDisposable
{
    private const double OverlayWidth = 330;
    private const double OverlayHeight = 34;
    private readonly int _processId;
    private readonly Window _window;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _trackingTimer;
    private bool _disposed;

    public StandardNavigationOverlay(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        _processId = processId;
        (_window, _text) = CreateWindow();
        _window.SourceInitialized += Window_SourceInitialized;
        _trackingTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Background,
            TrackingTimer_Tick,
            _window.Dispatcher);
        _trackingTimer.Start();
    }

    public void SetApplicationMenuActive(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _text.Text = active
            ? NavigationHintText.ApplicationMenu
            : NavigationHintText.Normal;
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

    private static (Window Window, TextBlock Text) CreateWindow()
    {
        var text = new TextBlock
        {
            Text = NavigationHintText.Normal,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new Border
        {
            Background = new SolidColorBrush(
                Color.FromArgb(224, 13, 19, 17)),
            BorderBrush = new SolidColorBrush(
                Color.FromRgb(100, 210, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = text,
            IsHitTestVisible = false,
        };

        var window = new Window
        {
            Width = OverlayWidth,
            Height = OverlayHeight,
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
        return (window, text);
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
        if (_disposed)
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
            if (clientWidth <= 0)
            {
                _window.Hide();
                return;
            }

            var overlayHandle =
                new WindowInteropHelper(_window).EnsureHandle();
            var dpi = Math.Max(
                NativeMethods.GetDpiForWindow(overlayHandle),
                96);
            var pixelsPerDip = dpi / 96d;
            var monitor = NativeMethods.MonitorFromWindow(targetWindow, 2);
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info) || !NativeMethods.GetWindowRect(targetWindow, out var bounds))
            {
                _window.Hide();
                return;
            }
            var workArea = new Rect(info.Work.Left / pixelsPerDip, info.Work.Top / pixelsPerDip,
                (info.Work.Right - info.Work.Left) / pixelsPerDip, (info.Work.Bottom - info.Work.Top) / pixelsPerDip);
            if (workArea.Width < OverlayWidth)
            {
                _window.Hide();
                return;
            }
            _window.Left = Math.Clamp((clientOrigin.X + clientWidth / 2d) / pixelsPerDip - OverlayWidth / 2d,
                workArea.Left, workArea.Right - OverlayWidth);
            _window.Top = bounds.Bottom / pixelsPerDip + 8;
            if (_window.Top + OverlayHeight > workArea.Bottom) _window.Top = bounds.Top / pixelsPerDip - OverlayHeight - 8;
            if (_window.Top < workArea.Top) { _window.Hide(); return; }
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
        internal struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
        }
        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out NativeRect rect);
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
