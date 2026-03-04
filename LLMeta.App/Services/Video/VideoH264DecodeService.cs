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

    private enum DecoderOutputPixelFormat
    {
        Unknown = 0,
        Bgra32 = 1,
        Nv12 = 2,
    }

    private readonly AppLogger _logger;

    private IMFTransform? _decoder;
    private bool _isStarted;
    private bool _outputTypeSet;
    private int _outputWidth;
    private int _outputHeight;
    private VideoCodecKind _activeCodecKind = VideoCodecKind.Unknown;
    private string _activeCodecName = "unknown";
    private DecoderOutputPixelFormat _outputPixelFormat = DecoderOutputPixelFormat.Unknown;
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
        _decoder?.Dispose();
        _decoder = null;
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
}
