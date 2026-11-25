#if WINDOWS
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace PhoneBox.Agent;

/// <summary>
/// The pictures of one window, through Windows Graphics Capture: the window's own content, even when other windows are
/// in front of it (a minimized window has none). Windows draws a frame around a window while it is captured. Each new
/// picture is copied to memory as BGRA and kept as the latest one; <see cref="Next"/> waits for it.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _arrived = new(0, 1);
    private Windows.Graphics.SizeInt32 _size;
    private Frame? _latest;
    private int _copying, _disposed;

    /// <summary>A copied picture: BGRA, <see cref="Stride"/> bytes a row. Returned to the capture with <see cref="Release"/>.</summary>
    internal sealed class Frame(int width, int height, int stride, byte[] pixels)
    {
        public int Width = width, Height = height, Stride = stride;
        public byte[] Pixels = pixels;
    }
    private Frame? _spare; // the buffer of the picture before last, reused for the next copy

    public static bool Supported => GraphicsCaptureSession.IsSupported();

    private WindowCapture(nint hwnd)
    {
        _device = CreateDevice();
        var item = CreateItem(hwnd);
        _size = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();
    }

    public static WindowCapture Start(nint hwnd) => new(hwnd);

    private async void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = pool.TryGetNextFrame();
            if (frame is null || Volatile.Read(ref _disposed) != 0) return;
            if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
            {
                // the window was resized: new buffers of the new size, and this picture is skipped
                _size = frame.ContentSize;
                pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                return;
            }
            // one copy at a time: a picture that comes while the last one is still being copied is dropped
            if (Interlocked.Exchange(ref _copying, 1) != 0) return;
            try
            {
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
                Store(bitmap);
            }
            finally { Volatile.Write(ref _copying, 0); }
        }
        catch (Exception) { /* a window that closed or a lost device: the stream notices that no pictures come */ }
        finally { frame?.Dispose(); }
    }

    private void Store(SoftwareBitmap bitmap)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        var native = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in IID_IMemoryBufferByteAccess, out var access));
        unsafe
        {
            byte* data; uint capacity;
            try
            {
                // IMemoryBufferByteAccess::GetBuffer, the first method after IUnknown's three
                var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)(*(void***)access)[3];
                Marshal.ThrowExceptionForHR(getBuffer(access, &data, &capacity));
            }
            finally { Marshal.Release(access); }
            var length = plane.Stride * plane.Height;
            if (plane.StartIndex + length > capacity) return;
            Frame target;
            lock (_gate) { target = _spare is { } s && s.Pixels.Length >= length ? s : new Frame(0, 0, 0, new byte[length]); _spare = null; }
            Marshal.Copy((nint)(data + plane.StartIndex), target.Pixels, 0, length);
            target.Width = plane.Width; target.Height = plane.Height; target.Stride = plane.Stride;
            lock (_gate)
            {
                if (_latest is not null) _spare ??= _latest;
                _latest = target;
            }
        }
        if (_arrived.CurrentCount == 0) try { _arrived.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>The newest picture since the last call, or null when none came within the wait.</summary>
    public async Task<Frame?> Next(TimeSpan wait, CancellationToken ct)
    {
        if (!await _arrived.WaitAsync(wait, ct)) return null;
        lock (_gate) { var f = _latest; _latest = null; return f; }
    }

    /// <summary>Hands a picture's buffer back for the next copy.</summary>
    public void Release(Frame frame) { lock (_gate) _spare ??= frame; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _pool.FrameArrived -= OnFrame; } catch { }
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
        _arrived.Dispose();
    }

    // ------------------------------------------------------------------ interop

    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IID_IGraphicsCaptureItemInterop, out var interopPtr));
        try
        {
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
            var itemPtr = interop.CreateForWindow(hwnd, in IID_IGraphicsCaptureItem);
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally { Marshal.Release(interopPtr); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1, D3D_DRIVER_TYPE_WARP = 5;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20, D3D11_SDK_VERSION = 7;
        var hr = D3D11CreateDevice(0, D3D_DRIVER_TYPE_HARDWARE, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0, 0, D3D11_SDK_VERSION, out var d3d, out _, out var context);
        if (hr < 0) hr = D3D11CreateDevice(0, D3D_DRIVER_TYPE_WARP, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0, 0, D3D11_SDK_VERSION, out d3d, out _, out context);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in IID_IDXGIDevice, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            if (context != 0) Marshal.Release(context);
            Marshal.Release(d3d);
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount,
        uint sdkVersion, out nint device, out int featureLevel, out nint immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    private static readonly Guid IID_IMemoryBufferByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
}
#endif
