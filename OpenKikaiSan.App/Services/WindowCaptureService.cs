using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OpenKikaiSan.App.Models;
using OpenKikaiSan.App.Utils;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace OpenKikaiSan.App.Services;

public sealed class WindowCaptureService : IDisposable
{
    private static readonly Guid Id3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly object _lock = new();
    private readonly AppLogger _logger;

    private IDirect3DDevice? _captureDevice;
    private GraphicsCaptureItem? _captureItem;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private nint _d3d11DevicePointer;
    private uint _sequence;
    private SizeInt32 _currentContentSize;
    private string _statusText = "Capture: not selected";
    private bool _loggedFirstFrame;

    public WindowCaptureService(AppLogger logger)
    {
        _logger = logger;
    }

    public bool IsCaptureActive
    {
        get
        {
            lock (_lock)
            {
                return _captureSession is not null;
            }
        }
    }

    public string GetStatusText()
    {
        lock (_lock)
        {
            return _statusText;
        }
    }

    public event Action<DecodedVideoFrame>? FrameCaptured;
    public event Action? CaptureStopped;

    public void SetD3D11DevicePointer(nint d3d11DevicePointer)
    {
        GraphicsCaptureItem? restartItem = null;
        string? logMessage = null;
        lock (_lock)
        {
            if (_d3d11DevicePointer == d3d11DevicePointer)
            {
                return;
            }

            _d3d11DevicePointer = d3d11DevicePointer;
            try
            {
                _captureDevice = CreateCaptureDevice(d3d11DevicePointer);
                logMessage =
                    d3d11DevicePointer == IntPtr.Zero
                        ? "Window capture D3D11 device cleared because OpenXR device pointer is zero."
                        : $"Window capture D3D11 device updated. pointer=0x{d3d11DevicePointer:X16}";
            }
            catch (Exception ex)
            {
                _captureDevice = null;
                _statusText = "Capture: D3D11 device unavailable";
                _logger.Error(
                    $"Window capture D3D11 device creation failed. pointer=0x{d3d11DevicePointer:X16}",
                    ex
                );
            }
            restartItem = _captureSession is not null ? _captureItem : null;
        }

        if (logMessage is not null)
        {
            _logger.Debug(logMessage);
        }

        if (restartItem is not null)
        {
            StartCapture(restartItem);
        }
    }

    public async Task<GraphicsCaptureItem?> PickCaptureItemAsync(Window ownerWindow)
    {
        var picker = new GraphicsCapturePicker();
        var ownerHwnd = new WindowInteropHelper(ownerWindow).Handle;
        InitializePickerWithWindow(picker, ownerHwnd);
        var item = await picker.PickSingleItemAsync();
        if (item is null)
        {
            lock (_lock)
            {
                _statusText = "Capture: selection canceled";
            }
            return null;
        }

        return item;
    }

    public bool StartCapture(GraphicsCaptureItem item)
    {
        return StartCaptureCore(item);
    }

    public void StopCapture()
    {
        var notifyStopped = false;
        lock (_lock)
        {
            notifyStopped = _captureItem is not null || _captureSession is not null;
            DisposeCaptureObjects();
            _captureItem = null;
            _statusText = "Capture: stopped";
        }

        if (notifyStopped)
        {
            CaptureStopped?.Invoke();
        }
    }

    public void Dispose()
    {
        StopCapture();
        lock (_lock)
        {
            _captureDevice = null;
        }
    }

    private bool StartCaptureCore(GraphicsCaptureItem item)
    {
        lock (_lock)
        {
            if (_captureDevice is null)
            {
                _statusText = "Capture: D3D11 device unavailable";
                _logger.Warn(
                    $"Window capture start blocked: D3D11 device unavailable. target={item.DisplayName} pointer=0x{_d3d11DevicePointer:X16}"
                );
                return false;
            }

            DisposeCaptureObjects();
            _captureItem = item;
            _captureItem.Closed += OnCaptureItemClosed;
            _sequence = 0;
            _currentContentSize = item.Size;
            _loggedFirstFrame = false;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _captureDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size
            );
            _framePool.FrameArrived += OnFrameArrived;
            _captureSession = _framePool.CreateCaptureSession(item);
            _captureSession.MinUpdateInterval = TimeSpan.Zero;
            _captureSession.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportAndRender;
            _captureSession.StartCapture();
            _statusText = $"Capture: {item.DisplayName} {item.Size.Width}x{item.Size.Height}";
            _logger.Info(
                $"Window capture started: target={item.DisplayName} size={item.Size.Width}x{item.Size.Height} minUpdateIntervalMs={_captureSession.MinUpdateInterval.TotalMilliseconds:0.###} dirtyRegionMode={_captureSession.DirtyRegionMode}"
            );
            return true;
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        var notifyStopped = false;
        lock (_lock)
        {
            notifyStopped = _captureItem is not null || _captureSession is not null;
            DisposeCaptureObjects();
            _captureItem = null;
            _statusText = "Capture: target closed";
        }

        if (notifyStopped)
        {
            CaptureStopped?.Invoke();
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var needsResize = false;
            var contentSize = frame.ContentSize;
            lock (_lock)
            {
                if (_captureItem is not null)
                {
                    needsResize =
                        contentSize.Width != _currentContentSize.Width
                        || contentSize.Height != _currentContentSize.Height;
                    if (needsResize && _captureDevice is not null)
                    {
                        _framePool?.Recreate(
                            _captureDevice,
                            DirectXPixelFormat.B8G8R8A8UIntNormalized,
                            2,
                            contentSize
                        );
                        _currentContentSize = contentSize;
                        _statusText =
                            $"Capture: {_captureItem.DisplayName} {contentSize.Width}x{contentSize.Height}";
                    }
                }
            }

            var texturePointer = GetTexturePointer(frame.Surface);
            if (texturePointer == IntPtr.Zero)
            {
                return;
            }

            Marshal.AddRef(texturePointer);

            var sequence = unchecked(++_sequence);
            var nowUnixMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var capturedFrame = new DecodedVideoFrame(
                sequence,
                nowUnixMs,
                nowUnixMs,
                contentSize.Width,
                contentSize.Height,
                texturePointer,
                0
            );
            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                _logger.Debug(
                    $"Window capture first frame: target={_captureItem?.DisplayName ?? "unknown"} size={contentSize.Width}x{contentSize.Height}"
                );
            }
            FrameCaptured?.Invoke(capturedFrame);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _statusText = $"Capture error: {ex.Message}";
            }
            _logger.Error("Window capture frame handling failed.", ex);
        }
    }

    private static IDirect3DDevice? CreateCaptureDevice(nint d3d11DevicePointer)
    {
        if (d3d11DevicePointer == IntPtr.Zero)
        {
            return null;
        }

        var hr = CreateDirect3D11DeviceFromDXGIDevice(d3d11DevicePointer, out var inspectable);
        if (hr < 0 || inspectable == IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private static nint GetTexturePointer(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        return access.GetInterface(Id3D11Texture2DGuid);
    }

    private void DisposeCaptureObjects()
    {
        if (_captureItem is not null)
        {
            _captureItem.Closed -= OnCaptureItemClosed;
        }

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _captureSession?.Dispose();
        _captureSession = null;
        _framePool?.Dispose();
        _framePool = null;
        _currentContentSize = default;
    }

    private static void InitializePickerWithWindow(GraphicsCapturePicker picker, nint ownerHwnd)
    {
        var initializeWithWindow = picker.As<IInitializeWithWindow>();
        initializeWithWindow.Initialize(ownerHwnd);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice
    );

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        void Initialize(nint hwnd);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(in Guid iid);
    }
}
