using System.Runtime.InteropServices;
using LLMeta.App.Models;
using LLMeta.App.Utils;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace LLMeta.App.Services;

public sealed partial class VideoH264DecodeService : IDisposable
{
    private const int MfTransformTypeNotSet = unchecked((int)0xC00D6D60);
    private const int DefaultInputWidth = 1920;
    private const int DefaultInputHeight = 1080;
    private const int DefaultInputFrameRateNumerator = 60;
    private const int DefaultInputFrameRateDenominator = 1;

    private enum VideoCodecKind
    {
        Unknown = 0,
        Vp8 = 1,
    }

    private readonly AppLogger _logger;

    private IMFTransform? _decoder;
    private IMFDXGIDeviceManager? _dxgiDeviceManager;
    private nint _d3d11DevicePointer;
    private bool _isStarted;
    private bool _outputTypeSet;
    private int _outputWidth;
    private int _outputHeight;
    private VideoCodecKind _activeCodecKind = VideoCodecKind.Unknown;
    private string _activeCodecName = "unknown";
    private long _sampleTime100Ns;
    private DecodedVideoFrame? _latestFrame;
    private readonly object _frameLock = new();
    private bool _loggedFirstDecodedFrame;

    public VideoH264DecodeService(AppLogger logger)
    {
        _logger = logger;
    }

    public bool TryGetLatestFrame(out DecodedVideoFrame frame)
    {
        lock (_frameLock)
        {
            if (_latestFrame is null)
            {
                frame = default;
                return false;
            }

            frame = _latestFrame.Value;
            _latestFrame = null;
            return true;
        }
    }

    public void SetD3D11DevicePointer(nint d3d11DevicePointer)
    {
        if (d3d11DevicePointer == _d3d11DevicePointer)
        {
            return;
        }

        _d3d11DevicePointer = d3d11DevicePointer;
        _dxgiDeviceManager?.Dispose();
        _dxgiDeviceManager = null;
    }

    public string Decode(VideoFramePacket packet)
    {
        try
        {
            EnsureStarted(packet.CodecName);
            if (_decoder is null)
            {
                return "decoder unavailable (" + packet.CodecName + ")";
            }

            using var sample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(packet.Payload.Length);

            buffer.Lock(out var pBuffer, out _, out _);
            try
            {
                Marshal.Copy(packet.Payload, 0, pBuffer, packet.Payload.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = packet.Payload.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = _sampleTime100Ns;
            sample.SampleDuration = 10_000_000 / DefaultInputFrameRateNumerator;
            _sampleTime100Ns += sample.SampleDuration;

            var inputStatus = _decoder.GetInputStatus(0);
            if ((inputStatus & (int)InputStatusFlags.InputStatusAcceptData) == 0)
            {
                _ = DrainOutputs(packet, out _);
            }

            _decoder.ProcessInput(0, sample, 0);
            var drained = DrainOutputs(packet, out var producedFrame);
            if (!drained)
            {
                return "need more input";
            }

            return producedFrame ? "decoded frame" : "drained no frame";
        }
        catch (Exception ex)
        {
            _logger.Error("Video decode failed.", ex);
            ResetDecoderAfterFailure();
            return "decode failed (" + packet.CodecName + "): " + ex.Message;
        }
    }

    public void Dispose()
    {
        ReleaseLatestFrameIfNeeded();
        _decoder?.Dispose();
        _decoder = null;
        _dxgiDeviceManager?.Dispose();
        _dxgiDeviceManager = null;
        if (_isStarted)
        {
            NativeMediaFoundation.MFShutdownChecked();
            _isStarted = false;
        }
    }

    private void EnsureStarted(string codecName)
    {
        var targetCodecKind = ParseCodecKind(codecName);
        if (targetCodecKind == VideoCodecKind.Unknown)
        {
            throw new InvalidOperationException("Unsupported video codec: " + codecName);
        }

        if (_decoder is not null && _activeCodecKind == targetCodecKind)
        {
            return;
        }

        if (_decoder is not null && _activeCodecKind != targetCodecKind)
        {
            _logger.Info(
                $"Video decoder reinitialize: {_activeCodecName} -> {NormalizeCodecName(codecName)}"
            );
            ResetDecoderAfterFailure();
        }

        if (!_isStarted)
        {
            NativeMediaFoundation.MFStartupFull();
            _isStarted = true;
            _logger.Info("Media Foundation started: full startup.");
        }

        var decoderTransform = CreateDecoderTransform(targetCodecKind, out var inputSubtype);
        if (decoderTransform is null)
        {
            throw new InvalidOperationException(
                "No decoder MFT was found for codec " + NormalizeCodecName(codecName) + "."
            );
        }
        _decoder = decoderTransform;
        ApplyDecoderLatencyAttributes();

        try
        {
            ApplyDecoderInputType(inputSubtype);
        }
        catch (SharpGenException ex) when (ex.HResult == MfTransformTypeNotSet)
        {
            _logger.Info(
                "Video decoder input type requires output type first. Trying output bootstrap."
            );
            if (!TrySetOutputType())
            {
                throw;
            }

            ApplyDecoderInputType(inputSubtype);
        }

        ConfigureDecoderD3D11DeviceManager();

        if (!TrySetOutputType())
        {
            throw new InvalidOperationException(
                "Decoder output type could not be applied for codec "
                    + NormalizeCodecName(codecName)
                    + "."
            );
        }

        _activeCodecKind = targetCodecKind;
        _activeCodecName = NormalizeCodecName(codecName);

        _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _logger.Info(
            $"Video decoder started: codec={_activeCodecName} inputSubtype={inputSubtype}"
        );
    }

    private void ApplyDecoderLatencyAttributes()
    {
        if (_decoder is null)
        {
            return;
        }

        try
        {
            using var attributes = _decoder.Attributes;
            if (attributes is null)
            {
                _logger.Info("Video decoder latency attributes skipped: attributes unavailable.");
                return;
            }

            attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u);
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            var isD3D11Aware = attributes.GetUInt32(TransformAttributeKeys.D3D11Aware) != 0;
            _logger.Info($"Video decoder latency attributes applied. d3d11Aware={isD3D11Aware}");
        }
        catch (Exception ex)
        {
            _logger.Info($"Video decoder latency attributes skipped: {ex.Message}");
        }
    }

    private void ConfigureDecoderD3D11DeviceManager()
    {
        if (_decoder is null)
        {
            return;
        }

        if (_d3d11DevicePointer == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "D3D11 device is not configured for decoder DXGI output."
            );
        }

        if (_dxgiDeviceManager is null)
        {
            _dxgiDeviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        }

        var dxgiDeviceManager =
            _dxgiDeviceManager
            ?? throw new InvalidOperationException("DXGI device manager is unavailable.");
        using var d3d11DeviceUnknown = new ComObject(_d3d11DevicePointer);
        dxgiDeviceManager.ResetDevice(d3d11DeviceUnknown);
        _decoder.ProcessMessage(
            TMessageType.MessageSetD3DManager,
            (UIntPtr)dxgiDeviceManager.NativePointer
        );
        _logger.Info("Video decoder D3D11 device manager configured.");
    }

    private void ReleaseLatestFrameIfNeeded()
    {
        lock (_frameLock)
        {
            if (_latestFrame is { SourceTexturePointer: not 0 } latestFrame)
            {
                Marshal.Release(latestFrame.SourceTexturePointer);
            }

            _latestFrame = null;
        }
    }
}
