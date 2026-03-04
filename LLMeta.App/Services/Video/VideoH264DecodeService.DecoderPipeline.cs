using System.Runtime.InteropServices;
using LLMeta.App.Models;
using Vortice.MediaFoundation;

namespace LLMeta.App.Services;

public sealed partial class VideoH264DecodeService
{
    private static readonly Guid Id3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid[] PreferredGpuOutputSubtypes = [VideoFormatGuids.NV12];

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
            var outputSample = CreateOutputSample(streamInfo);
            var outputBuffer = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = outputSample,
                Status = 0,
                Events = null!,
            };
            try
            {
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

                var dxgiBuffer = FindDxgiBuffer(outputBuffer.Sample);
                if (dxgiBuffer is null)
                {
                    throw new InvalidOperationException(
                        "Decoder output is not DXGI-backed. CPU fallback is disabled."
                    );
                }

                using (dxgiBuffer)
                {
                    var sourceTexturePointer = dxgiBuffer.GetResource(Id3D11Texture2DGuid);
                    if (sourceTexturePointer == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            "DXGI decode surface resource is unavailable."
                        );
                    }
                    Marshal.AddRef(sourceTexturePointer);

                    var frame = new DecodedVideoFrame(
                        packet.Sequence,
                        packet.TimestampUnixMs,
                        (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        _outputWidth,
                        _outputHeight,
                        sourceTexturePointer,
                        (uint)dxgiBuffer.SubresourceIndex
                    );
                    lock (_frameLock)
                    {
                        if (_latestFrame is { SourceTexturePointer: not 0 } previousFrame)
                        {
                            Marshal.Release(previousFrame.SourceTexturePointer);
                        }
                        _latestFrame = frame;
                    }
                    if (!_loggedFirstDecodedFrame)
                    {
                        _loggedFirstDecodedFrame = true;
                        _logger.Info(
                            "Video decoder first DXGI frame produced: "
                                + $"seq={packet.Sequence} width={_outputWidth} height={_outputHeight}"
                        );
                    }
                    producedFrame = true;
                }
            }
            finally
            {
                if (outputBuffer.Events is IDisposable outputEvents)
                {
                    outputEvents.Dispose();
                }

                if (
                    outputBuffer.Sample is not null
                    && !ReferenceEquals(outputBuffer.Sample, outputSample)
                )
                {
                    outputBuffer.Sample.Dispose();
                }

                outputSample?.Dispose();
            }
        }
    }

    private static IMFDXGIBuffer? FindDxgiBuffer(IMFSample sample)
    {
        var bufferCount = sample.BufferCount;
        for (var index = 0; index < bufferCount; index++)
        {
            IMFMediaBuffer? buffer = null;
            try
            {
                buffer = sample.GetBufferByIndex(index);
                if (buffer is null)
                {
                    continue;
                }

                var dxgiBuffer = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
                if (dxgiBuffer is not null)
                {
                    return dxgiBuffer;
                }
            }
            finally
            {
                buffer?.Dispose();
            }
        }

        return null;
    }

    private bool TrySetOutputType()
    {
        if (_decoder is null)
        {
            return false;
        }

        var availableSubtypes = new List<Guid>();
        var preferredSubtypeIndices = new Dictionary<Guid, int>();
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
                if (mediaType.GetGUID(MediaTypeAttributeKeys.MajorType) != MediaTypeGuids.Video)
                {
                    continue;
                }

                availableSubtypes.Add(subtype);
                if (
                    PreferredGpuOutputSubtypes.Contains(subtype)
                    && !preferredSubtypeIndices.ContainsKey(subtype)
                )
                {
                    preferredSubtypeIndices[subtype] = index;
                }
            }
        }

        foreach (var preferredSubtype in PreferredGpuOutputSubtypes)
        {
            if (!preferredSubtypeIndices.TryGetValue(preferredSubtype, out var typeIndex))
            {
                continue;
            }

            using var matchedType = _decoder.GetOutputAvailableType(0, typeIndex);
            if (matchedType is null)
            {
                continue;
            }

            if (ApplyOutputType(matchedType, preferredSubtype))
            {
                return true;
            }
        }

        if (availableSubtypes.Count > 0)
        {
            _logger.Info(
                "Video decoder output type candidates: "
                    + string.Join(
                        ", ",
                        availableSubtypes.Select(static subtype => DescribeOutputSubtype(subtype))
                    )
            );
        }

        return false;
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
        _outputTypeSet = _outputWidth > 0 && _outputHeight > 0;
        _logger.Info(
            "Video decoder output format ready: "
                + $"width={_outputWidth} height={_outputHeight} subtype={DescribeOutputSubtype(subtype)}"
        );
        return _outputTypeSet;
    }

    private static string DescribeOutputSubtype(Guid subtype)
    {
        if (subtype == VideoFormatGuids.NV12)
            return "NV12";
        if (subtype == VideoFormatGuids.P010)
            return "P010";
        if (subtype == VideoFormatGuids.P016)
            return "P016";
        if (subtype == VideoFormatGuids.YUY2)
            return "YUY2";
        if (subtype == VideoFormatGuids.I420)
            return "I420";
        if (subtype == VideoFormatGuids.Iyuv)
            return "IYUV";
        if (subtype == VideoFormatGuids.Yv12)
            return "YV12";
        if (subtype == VideoFormatGuids.Rgb32)
            return "RGB32";
        if (subtype == VideoFormatGuids.Argb32)
            return "ARGB32";

        return subtype.ToString();
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
        ReleaseLatestFrameIfNeeded();
        _decoder?.Dispose();
        _decoder = null;
        _outputTypeSet = false;
        _outputWidth = 0;
        _outputHeight = 0;
        _activeCodecKind = VideoCodecKind.Unknown;
        _activeCodecName = "unknown";
        _sampleTime100Ns = 0;
        _loggedFirstDecodedFrame = false;
    }
}
