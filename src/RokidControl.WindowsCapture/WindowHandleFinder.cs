using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RokidControl.WindowsCapture;

public static class WindowHandleFinder
{
    public static async Task<nint> WaitForMainWindowAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.StartNew();
        while (startedAt.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    "The target process exited before creating a window.");
            }

            if (process.MainWindowHandle != nint.Zero)
            {
                return process.MainWindowHandle;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new TimeoutException(
            "The target process did not create a main window.");
    }

    public static void PrepareForBackgroundCapture(nint windowHandle)
    {
        const int extendedStyleIndex = -20;
        const nint toolWindowStyle = 0x00000080;
        const nint appWindowStyle = 0x00040000;
        const uint noSize = 0x0001;
        const uint noActivate = 0x0010;
        const uint frameChanged = 0x0020;
        const uint noOwnerZOrder = 0x0200;

        var extendedStyle = GetWindowLongPointer(
            windowHandle,
            extendedStyleIndex);
        var updatedStyle =
            (extendedStyle & ~appWindowStyle) | toolWindowStyle;
        _ = SetWindowLongPointer(
            windowHandle,
            extendedStyleIndex,
            updatedStyle);
        if (!SetWindowPos(
                windowHandle,
                nint.Zero,
                -30_000,
                -30_000,
                0,
                0,
                noSize | noActivate | frameChanged | noOwnerZOrder))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The source window could not be moved off screen.");
        }
    }

    private static nint GetWindowLongPointer(nint windowHandle, int index) =>
        Environment.Is64BitProcess
            ? GetWindowLongPtr64(windowHandle, index)
            : GetWindowLong32(windowHandle, index);

    private static nint SetWindowLongPointer(
        nint windowHandle,
        int index,
        nint value) =>
        Environment.Is64BitProcess
            ? SetWindowLongPtr64(windowHandle, index, value)
            : SetWindowLong32(windowHandle, index, value);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongPtrW",
        SetLastError = true)]
    private static extern nint GetWindowLongPtr64(
        nint windowHandle,
        int index);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowLongW",
        SetLastError = true)]
    private static extern nint GetWindowLong32(
        nint windowHandle,
        int index);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetWindowLongPtrW",
        SetLastError = true)]
    private static extern nint SetWindowLongPtr64(
        nint windowHandle,
        int index,
        nint value);

    [DllImport(
        "user32.dll",
        EntryPoint = "SetWindowLongW",
        SetLastError = true)]
    private static extern nint SetWindowLong32(
        nint windowHandle,
        int index,
        nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
