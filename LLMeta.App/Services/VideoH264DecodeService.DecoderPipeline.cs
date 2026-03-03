using System.Runtime.InteropServices;
using LLMeta.App.Models;
using Vortice.MediaFoundation;

namespace LLMeta.App.Services;

public sealed partial class VideoH264DecodeService
{
    private void ApplyDecoderInputType(Guid inputSubtype)
    {
        if (_decoder is null)
        {
            throw new InvalidOperationException("Decoder is not initialized.");
        }

        Exception? lastError = null;
        for (var index = 0; ; index++)
        {
            IMFMediaType? availableType = null;
            try
            {
                availableType = _decoder.GetInputAvailableType(0, index);
            }
            catch
            {
                break;
            }

            using (availableType)
            {
                Guid majorType;
                Guid subtype;
                try
                {
                    majorType = availableType.GetGUID(MediaTypeAttributeKeys.MajorType);
                    subtype = availableType.GetGUID(MediaTypeAttributeKeys.Subtype);
                }
                catch
                {
                    continue;
                }

                if (majorType != MediaTypeGuids.Video || subtype != inputSubtype)
                {
                    continue;
                }

                ApplyCommonInputVideoAttributes(availableType);
                try
                {
                    _decoder.SetInputType(0, availableType, (int)SetTypeFlags.None);
                    _logger.Info(
                        "Video decoder input format ready: "
                            + $"subtype={DescribeInputSubtype(new RegisterTypeInfo { GuidMajorType = majorType, GuidSubtype = subtype })}"
                            + $" width={DefaultInputWidth} height={DefaultInputHeight}"
                            + $" fps={DefaultInputFrameRateNumerator}/{DefaultInputFrameRateDenominator}"
                    );
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.Info(
                        "Video decoder input type rejected: "
                            + $"index={index} subtype={DescribeInputSubtype(new RegisterTypeInfo { GuidMajorType = majorType, GuidSubtype = subtype })}"
                            + $" error={ex.Message}"
                    );
                }
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException(
                "Decoder input type could not be applied for subtype " + inputSubtype + ".",
                lastError
            );
        }

        throw new InvalidOperationException(
            "Decoder input type was not found for subtype " + inputSubtype + "."
        );
    }

    private static void ApplyCommonInputVideoAttributes(IMFMediaType mediaType)
    {
        mediaType.Set(
            MediaTypeAttributeKeys.FrameSize,
            PackRatio(DefaultInputWidth, DefaultInputHeight)
        );
        mediaType.Set(
            MediaTypeAttributeKeys.FrameRate,
            PackRatio(DefaultInputFrameRateNumerator, DefaultInputFrameRateDenominator)
        );
        mediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
        mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2);
    }

    private static ulong PackRatio(int numerator, int denominator)
    {
        return ((ulong)(uint)numerator << 32) | (uint)denominator;
    }

    private bool DrainOutputs(VideoFramePacket packet, out bool producedFrame)
    {
        producedFrame = false;
        if (_decoder is null)
        {
            return false;
        }

        while (true)
        {
            if (!_outputTypeSet)
            {
                if (!TrySetOutputType())
                {
                    return false;
                }
            }

            var streamInfo = _decoder.GetOutputStreamInfo(0);
            using var outputSample = CreateOutputSample(streamInfo);
            var outputBuffer = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = outputSample,
                Status = 0,
                Events = null!,
            };

            var result = _decoder.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref outputBuffer,
                out _
            );
            if (result == ResultCode.TransformNeedMoreInput)
            {
                return true;
            }

            if (result == ResultCode.TransformStreamChange)
            {
                _outputTypeSet = false;
                continue;
            }

            if (result.Failure)
            {
                throw new InvalidOperationException("ProcessOutput failed: " + result);
            }

            if (outputBuffer.Sample is null)
            {
                return true;
            }

            var contiguous = outputBuffer.Sample.ConvertToContiguousBuffer();
            if (contiguous is null)
            {
                return true;
            }

            using (contiguous)
            {
                contiguous.Lock(out var pData, out _, out var currentLength);
                try
                {
                    var bytes = new byte[currentLength];
                    Marshal.Copy(pData, bytes, 0, currentLength);
                    var bgra = _outputPixelFormat switch
                    {
                        DecoderOutputPixelFormat.Bgra32 => ConvertRgb32ToBgra(
                            bytes,
                            _outputWidth,
                            _outputHeight
                        ),
                        DecoderOutputPixelFormat.Nv12 => ConvertNv12ToBgra(
                            bytes,
                            _outputWidth,
                            _outputHeight
                        ),
                        _ => throw new InvalidOperationException(
                            "Unsupported output pixel format."
                        ),
                    };
                    var frame = new DecodedVideoFrame(
                        packet.Sequence,
                        packet.TimestampUnixMs,
                        _outputWidth,
                        _outputHeight,
                        bgra
                    );
                    lock (_frameLock)
                    {
                        _latestFrame = frame;
                    }
                    if (!_loggedFirstDecodedFrame)
                    {
                        _loggedFirstDecodedFrame = true;
                        _logger.Info(
                            "Video decoder first frame produced: "
                                + $"seq={packet.Sequence} width={_outputWidth} height={_outputHeight}"
                        );
                    }
                    producedFrame = true;
                }
                finally
                {
                    contiguous.Unlock();
                }
            }
        }
    }

    private bool TrySetOutputType()
    {
        if (_decoder is null)
        {
            return false;
        }

        var firstNv12Index = -1;
        for (var index = 0; ; index++)
        {
            IMFMediaType? mediaType = null;
            try
            {
                mediaType = _decoder.GetOutputAvailableType(0, index);
            }
            catch
            {
                break;
            }

            using (mediaType)
            {
                var subtype = mediaType.GetGUID(MediaTypeAttributeKeys.Subtype);
                if (subtype == VideoFormatGuids.Rgb32)
                {
                    return ApplyOutputType(mediaType, subtype);
                }

                if (subtype == VideoFormatGuids.NV12 && firstNv12Index < 0)
                {
                    firstNv12Index = index;
                }
            }
        }

        if (firstNv12Index < 0)
        {
            return false;
        }

        using var nv12Type = _decoder.GetOutputAvailableType(0, firstNv12Index);
        var nv12Subtype = nv12Type.GetGUID(MediaTypeAttributeKeys.Subtype);
        return ApplyOutputType(nv12Type, nv12Subtype);
    }

    private bool ApplyOutputType(IMFMediaType mediaType, Guid subtype)
    {
        if (_decoder is null)
        {
            return false;
        }

        _decoder.SetOutputType(0, mediaType, (int)SetTypeFlags.None);
        var frameSize = mediaType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        _outputWidth = (int)(frameSize >> 32);
        _outputHeight = (int)(frameSize & 0xFFFFFFFF);
        _outputPixelFormat =
            subtype == VideoFormatGuids.Rgb32
                ? DecoderOutputPixelFormat.Bgra32
                : DecoderOutputPixelFormat.Nv12;
        _outputTypeSet = _outputWidth > 0 && _outputHeight > 0;
        _logger.Info(
            "Video decoder output format ready: "
                + $"width={_outputWidth} height={_outputHeight} subtype={subtype}"
        );
        return _outputTypeSet;
    }

    private static IMFSample CreateOutputSample(OutputStreamInfo streamInfo)
    {
        if ((streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0)
        {
            return null!;
        }

        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(streamInfo.Size);
        sample.AddBuffer(buffer);
        return sample;
    }

    private void ResetDecoderAfterFailure()
    {
        _decoder?.Dispose();
        _decoder = null;
        _outputTypeSet = false;
        _outputWidth = 0;
        _outputHeight = 0;
        _activeCodecKind = VideoCodecKind.Unknown;
        _activeCodecName = "unknown";
        _outputPixelFormat = DecoderOutputPixelFormat.Unknown;
        _sampleTime100Ns = 0;
        _loggedFirstDecodedFrame = false;
    }
}
