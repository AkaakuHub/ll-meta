using Vortice.MediaFoundation;

namespace LLMeta.App.Services;

public sealed partial class VideoH264DecodeService
{
    private IMFTransform? CreateDecoderTransform(
        VideoCodecKind codecKind,
        out Guid selectedInputSubtype
    )
    {
        var inputSubtypeCandidates = GetInputSubtypeCandidates(codecKind);
        selectedInputSubtype = Guid.Empty;
        RegisterTypeInfo?[] outputCandidates =
        [
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video },
            null,
        ];

        var hardwarePreferredFlags = (uint)(
            EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter
        );
        foreach (var inputSubtype in inputSubtypeCandidates)
        {
            var inputCandidate = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = inputSubtype,
            };
            foreach (var outputCandidate in outputCandidates)
            {
                var decoder = EnumerateAndActivateDecoder(
                    hardwarePreferredFlags,
                    inputCandidate,
                    outputCandidate
                );
                if (decoder is not null)
                {
                    _logger.Info(
                        "Video decoder selected: hardware-preferred MFT."
                            + $" inputSubtype={DescribeInputSubtype(inputCandidate)}"
                            + $" outputFilter={DescribeOutputFilter(outputCandidate)}"
                    );
                    selectedInputSubtype = inputSubtype;
                    return decoder;
                }
            }
        }

        var broadFlags = (uint)EnumFlag.EnumFlagAll;
        foreach (var inputSubtype in inputSubtypeCandidates)
        {
            var inputCandidate = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = inputSubtype,
            };
            foreach (var outputCandidate in outputCandidates)
            {
                var decoder = EnumerateAndActivateDecoder(
                    broadFlags,
                    inputCandidate,
                    outputCandidate
                );
                if (decoder is not null)
                {
                    _logger.Info(
                        "Video decoder selected: broad-enumeration MFT."
                            + $" inputSubtype={DescribeInputSubtype(inputCandidate)}"
                            + $" outputFilter={DescribeOutputFilter(outputCandidate)}"
                    );
                    selectedInputSubtype = inputSubtype;
                    return decoder;
                }
            }
        }

        return null;
    }

    private static Guid[] GetInputSubtypeCandidates(VideoCodecKind codecKind)
    {
        return codecKind switch
        {
            VideoCodecKind.Vp8 => [new Guid("30385056-0000-0010-8000-00AA00389B71")],
            _ => [],
        };
    }

    private static VideoCodecKind ParseCodecKind(string codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return VideoCodecKind.Unknown;
        }

        var normalized = NormalizeCodecName(codecName);
        return normalized switch
        {
            "VP8" => VideoCodecKind.Vp8,
            _ => VideoCodecKind.Unknown,
        };
    }

    private static string NormalizeCodecName(string codecName)
    {
        return codecName.Trim().ToUpperInvariant() switch
        {
            "H264" => "H264",
            "VP8" => "VP8",
            "VP9" => "VP9",
            "AV1" => "AV1",
            var unknown => unknown,
        };
    }

    private static IMFTransform? EnumerateAndActivateDecoder(
        uint flags,
        RegisterTypeInfo? inputType,
        RegisterTypeInfo? outputType
    )
    {
        using var activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            flags,
            inputType,
            outputType
        );
        foreach (var activate in activates)
        {
            IMFTransform? decoder = null;
            try
            {
                decoder = activate.ActivateObject<IMFTransform>();
                if (decoder is null)
                {
                    continue;
                }

                var subtype = inputType?.GuidSubtype ?? Guid.Empty;
                if (subtype == Guid.Empty || !CanApplyInputType(decoder, subtype))
                {
                    decoder.Dispose();
                    continue;
                }

                return decoder;
            }
            catch
            {
                decoder?.Dispose();
            }
        }

        return null;
    }

    private static bool CanApplyInputType(IMFTransform decoder, Guid inputSubtype)
    {
        for (var index = 0; ; index++)
        {
            IMFMediaType? availableType = null;
            try
            {
                availableType = decoder.GetInputAvailableType(0, index);
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

                try
                {
                    ApplyCommonInputVideoAttributes(availableType);
                    decoder.SetInputType(0, availableType, 1);
                    return true;
                }
                catch { }
            }
        }

        return false;
    }

    private static string DescribeInputSubtype(RegisterTypeInfo? inputType)
    {
        if (inputType is null)
        {
            return "null";
        }

        var subtype = inputType.Value.GuidSubtype;
        if (subtype == new Guid("30385056-0000-0010-8000-00AA00389B71"))
            return "VP8";

        return subtype.ToString();
    }

    private static string DescribeOutputFilter(RegisterTypeInfo? outputType)
    {
        return outputType is null ? "null" : "video-major";
    }
}
