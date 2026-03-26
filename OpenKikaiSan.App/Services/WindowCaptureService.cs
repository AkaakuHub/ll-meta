using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OpenKikaiSan.App.Models;
using OpenKikaiSan.App.Utils;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using CaptureDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
using CaptureDirect3DSurface = Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface;
using CapturePixelFormat = Windows.Graphics.DirectX.DirectXPixelFormat;

namespace OpenKikaiSan.App.Services;

public sealed class WindowCaptureService : IDisposable
{
    private static readonly Guid Id3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly object _lock = new();
    private readonly AppLogger _logger;
    private nint _d3d11DevicePointerForCopy;
    private nint _d3d11DeviceContextPointerForCopy;

    private CaptureDirect3DDevice? _captureDevice;
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
                SetDirect3D11Device(d3d11DevicePointer);
                logMessage =
                    d3d11DevicePointer == IntPtr.Zero
                        ? "Window capture D3D11 device cleared because OpenXR device pointer is zero."
                        : $"Window capture D3D11 device updated. pointer=0x{d3d11DevicePointer:X16}";
            }
            catch (Exception ex)
            {
                _captureDevice = null;
                SetDirect3D11Device(IntPtr.Zero);
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
            SetDirect3D11Device(IntPtr.Zero);
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
                CapturePixelFormat.B8G8R8A8UIntNormalized,
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
                            CapturePixelFormat.B8G8R8A8UIntNormalized,
                            2,
                            contentSize
                        );
                        _currentContentSize = contentSize;
                        _statusText =
                            $"Capture: {_captureItem.DisplayName} {contentSize.Width}x{contentSize.Height}";
                    }
                }
            }

            var sourceTexturePointer = GetTexturePointer(frame.Surface);
            if (sourceTexturePointer == IntPtr.Zero)
            {
                return;
            }

            var stableTexturePointer = CreateStableTextureCopy(sourceTexturePointer);
            Marshal.Release(sourceTexturePointer);
            if (stableTexturePointer == IntPtr.Zero)
            {
                return;
            }

            var sequence = unchecked(++_sequence);
            var nowUnixMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var capturedFrame = new DecodedVideoFrame(
                sequence,
                nowUnixMs,
                nowUnixMs,
                contentSize.Width,
                contentSize.Height,
                stableTexturePointer,
                0
            );
            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                _logger.Debug(
                    $"Window capture first frame: target={_captureItem?.DisplayName ?? "unknown"} size={contentSize.Width}x{contentSize.Height}"
                );
            }
            var frameCaptured = FrameCaptured;
            if (frameCaptured is null)
            {
                Marshal.Release(stableTexturePointer);
                return;
            }

            frameCaptured.Invoke(capturedFrame);
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

    private static CaptureDirect3DDevice? CreateCaptureDevice(nint d3d11DevicePointer)
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
            return MarshalInterface<CaptureDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private static nint GetTexturePointer(CaptureDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        return access.GetInterface(Id3D11Texture2DGuid);
    }

    private unsafe nint CreateStableTextureCopy(nint sourceTexturePointer)
    {
        lock (_lock)
        {
            if (
                _d3d11DevicePointerForCopy == IntPtr.Zero
                || _d3d11DeviceContextPointerForCopy == IntPtr.Zero
            )
            {
                return IntPtr.Zero;
            }
        }

        var d3d11Device = (ID3D11Device*)_d3d11DevicePointerForCopy;
        var d3d11DeviceContext = (ID3D11DeviceContext*)_d3d11DeviceContextPointerForCopy;
        var sourceTexture = (ID3D11Texture2D*)sourceTexturePointer;
        Texture2DDesc textureDesc = default;
        sourceTexture->GetDesc(&textureDesc);

        textureDesc.Usage = Usage.Default;
        textureDesc.CPUAccessFlags = 0;
        textureDesc.MiscFlags = 0;

        ID3D11Texture2D* copiedTexture = null;
        lock (_lock)
        {
            if (
                _d3d11DevicePointerForCopy == IntPtr.Zero
                || _d3d11DeviceContextPointerForCopy == IntPtr.Zero
            )
            {
                return IntPtr.Zero;
            }

            d3d11Device = (ID3D11Device*)_d3d11DevicePointerForCopy;
            d3d11DeviceContext = (ID3D11DeviceContext*)_d3d11DeviceContextPointerForCopy;
            var createResult = d3d11Device->CreateTexture2D(
                ref textureDesc,
                (SubresourceData*)0,
                ref copiedTexture
            );
            if (createResult < 0 || copiedTexture is null)
            {
                _logger.Debug(
                    $"Window capture texture copy allocation failed: hr=0x{createResult:X8} size={textureDesc.Width}x{textureDesc.Height} fmt={(int)textureDesc.Format}"
                );
                return IntPtr.Zero;
            }

            d3d11DeviceContext->CopyResource(
                (ID3D11Resource*)copiedTexture,
                (ID3D11Resource*)sourceTexture
            );
        }

        return (nint)copiedTexture;
    }

    private unsafe void SetDirect3D11Device(nint devicePointer)
    {
        if (_d3d11DeviceContextPointerForCopy != IntPtr.Zero)
        {
            _ = ((ID3D11DeviceContext*)_d3d11DeviceContextPointerForCopy)->Release();
            _d3d11DeviceContextPointerForCopy = IntPtr.Zero;
        }

        if (_d3d11DevicePointerForCopy != IntPtr.Zero)
        {
            _ = ((ID3D11Device*)_d3d11DevicePointerForCopy)->Release();
            _d3d11DevicePointerForCopy = IntPtr.Zero;
        }

        if (devicePointer == IntPtr.Zero)
        {
            return;
        }

        var device = (ID3D11Device*)devicePointer;
        _ = device->AddRef();
        _d3d11DevicePointerForCopy = devicePointer;

        ID3D11DeviceContext* deviceContext = null;
        device->GetImmediateContext(ref deviceContext);
        _d3d11DeviceContextPointerForCopy = (nint)deviceContext;
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
