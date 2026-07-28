using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

internal readonly record struct CapturedFrameResult(
    int Width,
    int Height,
    TimeSpan Elapsed);

internal static class GraphicsCaptureProbe
{
    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static async Task<CapturedFrameResult> CaptureOneFrameAsync(
        nint windowHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var item = CreateItemForWindow(windowHandle);
        using var device = Direct3DDeviceFactory.Create();
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        using var session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;

        var startedAt = Stopwatch.StartNew();
        var frameSource =
            new TaskCompletionSource<CapturedFrameResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        framePool.FrameArrived += OnFrameArrived;
        try
        {
            session.StartCapture();
            return await frameSource.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            framePool.FrameArrived -= OnFrameArrived;
        }

        void OnFrameArrived(
            Direct3D11CaptureFramePool sender,
            object eventArguments)
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                var size = frame.ContentSize;
                frameSource.TrySetResult(
                    new CapturedFrameResult(
                        size.Width,
                        size.Height,
                        startedAt.Elapsed));
            }
            catch (Exception exception)
            {
                frameSource.TrySetException(exception);
            }
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint windowHandle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForWindow(
            windowHandle,
            GraphicsCaptureItemGuid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(
            [In] nint windowHandle,
            in Guid interfaceId);

        nint CreateForMonitor(
            [In] nint monitorHandle,
            in Guid interfaceId);
    }
}

internal static class Direct3DDeviceFactory
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private static readonly Guid DxgiDeviceGuid =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static IDirect3DDevice Create()
    {
        var result = D3D11CreateDevice(
            nint.Zero,
            D3DDriverType.Hardware,
            nint.Zero,
            D3D11CreateDeviceBgraSupport,
            nint.Zero,
            0,
            D3D11SdkVersion,
            out var nativeDevice,
            out _,
            out var deviceContext);
        Marshal.ThrowExceptionForHR(result);

        try
        {
            var interfaceId = DxgiDeviceGuid;
            result = Marshal.QueryInterface(
                nativeDevice,
                in interfaceId,
                out var dxgiDevice);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                result = CreateDirect3D11DeviceFromDxgiDevice(
                    dxgiDevice,
                    out var inspectableDevice);
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(
                        inspectableDevice);
                }
                finally
                {
                    Marshal.Release(inspectableDevice);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(deviceContext);
            Marshal.Release(nativeDevice);
        }
    }

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static extern int D3D11CreateDevice(
        nint adapter,
        D3DDriverType driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint featureLevel,
        out nint immediateContext);

    [DllImport(
        "d3d11.dll",
        EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDxgiDevice(
        nint dxgiDevice,
        out nint graphicsDevice);

    private enum D3DDriverType : uint
    {
        Hardware = 1,
    }
}
