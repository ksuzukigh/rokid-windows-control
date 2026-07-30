using RokidControl.Core.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace RokidControl.WindowsCapture;

public sealed class CapturedFrameEventArgs : EventArgs
{
    public CapturedFrameEventArgs(BgraFrame frame)
    {
        Frame = frame;
    }

    public BgraFrame Frame { get; }
}

public sealed class CaptureFailedEventArgs : EventArgs
{
    public CaptureFailedEventArgs(Exception exception)
    {
        Exception = exception;
    }

    public Exception Exception { get; }
}

public sealed class WindowCaptureSession : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private int _processingFrame;
    private int _disposed;

    private WindowCaptureSession(nint windowHandle)
    {
        _item = GraphicsCaptureInterop.CreateItemForWindow(windowHandle);
        _device = Direct3DDeviceFactory.Create();
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _item.Size);
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _framePool.FrameArrived += FramePool_FrameArrived;
    }

    public event EventHandler<CapturedFrameEventArgs>? FrameArrived;

    public event EventHandler<CaptureFailedEventArgs>? Failed;

    public static WindowCaptureSession Start(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException(
                "A source window handle is required.",
                nameof(windowHandle));
        }

        var capture = new WindowCaptureSession(windowHandle);
        try
        {
            capture._session.StartCapture();
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _framePool.FrameArrived -= FramePool_FrameArrived;
        _session.Dispose();
        _framePool.Dispose();
        _device.Dispose();
    }

    private async void FramePool_FrameArrived(
        Direct3D11CaptureFramePool sender,
        object eventArguments)
    {
        if (Interlocked.Exchange(ref _processingFrame, 1) != 0)
        {
            using var skippedFrame = sender.TryGetNextFrame();
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();
            var size = frame.ContentSize;
            var pixels = await SoftwareBitmapReader.CopyBgraAsync(
                frame.Surface);
            if (Volatile.Read(ref _disposed) == 0)
            {
                FrameArrived?.Invoke(
                    this,
                    new CapturedFrameEventArgs(
                        new BgraFrame(size.Width, size.Height, pixels)));
            }
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Failed?.Invoke(this, new CaptureFailedEventArgs(exception));
            }
        }
        finally
        {
            Volatile.Write(ref _processingFrame, 0);
        }
    }
}
