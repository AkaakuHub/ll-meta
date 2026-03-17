using System.Runtime.InteropServices;
using LLMeta.App.Models;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private static readonly Guid Id3D11VideoDeviceGuid = new(
        "10EC4D5B-975A-4689-B9E4-D0AAC30FE333"
    );
    private static readonly Guid Id3D11VideoContextGuid = new(
        "61F21C45-3C0E-4A74-9CEA-67100D9AD5E4"
    );
    private const int StereoViewCount = 2;
    private const long DxgiFormatB8G8R8A8Unorm = 87;
    private const long DxgiFormatB8G8R8A8UnormSrgb = 91;
    private const long DxgiFormatR8G8B8A8Unorm = 28;
    private const long DxgiFormatR8G8B8A8UnormSrgb = 29;
    private const long DxgiFormatNv12 = 103;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    private readonly object _videoFrameLock = new();
    private readonly Swapchain[] _colorSwapchains = new Swapchain[StereoViewCount];
    private readonly SwapchainImageD3D11KHR[][] _swapchainImages = new SwapchainImageD3D11KHR[
        StereoViewCount
    ][];
    private readonly ViewConfigurationView[] _viewConfigurationViews = new ViewConfigurationView[
        StereoViewCount
    ];
    private readonly View[] _views = new View[StereoViewCount];
    private readonly uint[] _swapchainRenderWidths = new uint[StereoViewCount];
    private readonly uint[] _swapchainRenderHeights = new uint[StereoViewCount];
    private Swapchain _placeholderQuadSwapchain;
    private SwapchainImageD3D11KHR[] _placeholderQuadSwapchainImages = [];
    private uint _placeholderQuadSwapchainWidth;
    private uint _placeholderQuadSwapchainHeight;
    private long _colorSwapchainFormat;
    private string _swapchainFormatSummary = string.Empty;
    private ulong _requiredGraphicsAdapterLuid;
    private bool _hasRequiredGraphicsAdapterLuid;
    private string _graphicsAdapterSummary = string.Empty;
    private string _requestedSwapchainFormatLabel = "Auto";
    private string _selectedSwapchainFormatLabel = "unselected";
    private string _videoProcessorProbeSummary = "not-probed";
    private List<string> _availableSwapchainFormatLabels = ["Auto", "RGBA8", "BGRA8"];
    private string _requestedGraphicsAdapterLabel = "Auto";
    private string _selectedGraphicsAdapterLabel = "unselected";
    private List<string> _availableGraphicsAdapterLabels = ["Auto"];
    private string _requestedGraphicsBackendLabel = "D3D11";
    private string _selectedGraphicsBackendLabel = "unselected";
    private List<string> _availableGraphicsBackends = ["D3D11"];

    private ID3D11VideoDevice* _d3d11VideoDevice;
    private ID3D11VideoContext* _d3d11VideoContext;
    private ID3D11VideoProcessorEnumerator* _videoProcessorEnumerator;
    private ID3D11VideoProcessor* _videoProcessor;
    private ID3D11Texture2D* _videoProcessorOutputTexture;
    private ID3D11VideoProcessorOutputView* _videoProcessorOutputView;
    private ID3D11VideoProcessorInputView* _videoProcessorInputView;
    private ID3D11Texture2D* _blackClearTexture;
    private uint _blackClearTextureWidth;
    private uint _blackClearTextureHeight;
    private Format _blackClearTextureFormat;
    private ID3D11Texture2D* _placeholderTexture;
    private uint _placeholderTextureWidth;
    private uint _placeholderTextureHeight;
    private Format _placeholderTextureFormat;
    private nint _videoProcessorInputTexturePointer;
    private uint _videoProcessorInputSubresourceIndex;
    private uint _videoProcessorInputWidth;
    private uint _videoProcessorInputHeight;
    private uint _videoProcessorOutputWidth;
    private uint _videoProcessorOutputHeight;
    private Format _videoProcessorOutputFormat;
    private VideoUsage _videoProcessorUsage = VideoUsage.OptimalSpeed;

    private nint _latestSbsSourceTexturePointer;
    private uint _latestSbsSourceSubresourceIndex;
    private ulong _latestSbsTimestampUnixMs;
    private ulong _latestSbsDecodedUnixMs;
    private int _latestSbsWidth;
    private int _latestSbsHeight;
    private int _latestSbsVisibleHeight;
    private uint _latestVideoSequence;
    private OpenXrVideoRenderStats _videoRenderStats;
    private string _lastVideoProcessorFailureDetail = string.Empty;

    private void ReleaseLatestVideoTexture()
    {
        lock (_videoFrameLock)
        {
            if (_latestSbsSourceTexturePointer != IntPtr.Zero)
            {
                Marshal.Release(_latestSbsSourceTexturePointer);
                _latestSbsSourceTexturePointer = IntPtr.Zero;
            }

            _latestSbsSourceSubresourceIndex = 0;
            _latestSbsTimestampUnixMs = 0;
            _latestSbsDecodedUnixMs = 0;
            _latestSbsWidth = 0;
            _latestSbsHeight = 0;
            _latestSbsVisibleHeight = 0;
            _latestVideoSequence = 0;
            _videoRenderStats = default;
            _lastVideoProcessorFailureDetail = string.Empty;
        }
    }

    public OpenXrVideoRenderStats GetVideoRenderStatsSnapshot()
    {
        lock (_videoFrameLock)
        {
            return _videoRenderStats;
        }
    }

    private Result InitializeStereoRendering()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        uint viewCount = 0;
        var enumerateViewsResult = _xr.EnumerateViewConfigurationView(
            _instance,
            _systemId,
            ViewConfigurationType.PrimaryStereo,
            0,
            ref viewCount,
            (ViewConfigurationView*)0
        );
        if (enumerateViewsResult != Result.Success)
        {
            return enumerateViewsResult;
        }

        if (viewCount != StereoViewCount)
        {
            return Result.ErrorValidationFailure;
        }

        for (var i = 0; i < StereoViewCount; i++)
        {
            _viewConfigurationViews[i] = new ViewConfigurationView
            {
                Type = StructureType.ViewConfigurationView,
            };
            _views[i] = new View { Type = StructureType.View };
        }

        fixed (ViewConfigurationView* viewsPointer = _viewConfigurationViews)
        {
            enumerateViewsResult = _xr.EnumerateViewConfigurationView(
                _instance,
                _systemId,
                ViewConfigurationType.PrimaryStereo,
                viewCount,
                ref viewCount,
                viewsPointer
            );
            if (enumerateViewsResult != Result.Success)
            {
                return enumerateViewsResult;
            }
        }

        uint formatCount = 0;
        var formatResult = _xr.EnumerateSwapchainFormats(_session, 0, ref formatCount, (long*)0);
        if (formatResult != Result.Success)
        {
            return formatResult;
        }

        var formats = new long[formatCount];
        fixed (long* formatsPointer = formats)
        {
            formatResult = _xr.EnumerateSwapchainFormats(
                _session,
                formatCount,
                ref formatCount,
                formatsPointer
            );
            if (formatResult != Result.Success)
            {
                return formatResult;
            }
        }

        var bgraSupported = formats.Any(static format => IsBgraFamilyFormat(format));
        var rgbaSupported = formats.Any(static format => IsRgbaFamilyFormat(format));

        _swapchainFormatSummary =
            "available="
            + string.Join(", ", formats.Select(static format => DescribeSwapchainFormat(format)));
        lock (_videoFrameLock)
        {
            _availableSwapchainFormatLabels = ["Auto"];
            if (formats.Any(static format => IsRgbaFamilyFormat(format)))
            {
                _availableSwapchainFormatLabels.Add("RGBA8");
            }
            if (formats.Any(static format => IsBgraFamilyFormat(format)))
            {
                _availableSwapchainFormatLabels.Add("BGRA8");
            }
        }
        if (_graphicsAdapterSummary.Length > 0)
        {
            _swapchainFormatSummary = $"{_swapchainFormatSummary}, {_graphicsAdapterSummary}";
        }
        if (!bgraSupported && !rgbaSupported)
        {
            return Result.ErrorSwapchainFormatUnsupported;
        }

        if (
            !TrySelectSwapchainFormatForNv12VideoProcessor(
                formats,
                out _colorSwapchainFormat,
                out var probeSummary
            )
        )
        {
            lock (_videoFrameLock)
            {
                _selectedSwapchainFormatLabel = "unselected";
                _videoProcessorProbeSummary = probeSummary;
            }
            _swapchainFormatSummary = $"{_swapchainFormatSummary}, {probeSummary}";
            return Result.ErrorSwapchainFormatUnsupported;
        }

        lock (_videoFrameLock)
        {
            _selectedSwapchainFormatLabel = ToUiSwapchainLabel(_colorSwapchainFormat);
            _videoProcessorProbeSummary = probeSummary;
            _selectedGraphicsBackendLabel = "D3D11";
        }
        _swapchainFormatSummary = $"{_swapchainFormatSummary}, {probeSummary}";
        for (var eye = 0; eye < StereoViewCount; eye++)
        {
            var viewConfig = _viewConfigurationViews[eye];
            var swapchainWidth = viewConfig.RecommendedImageRectWidth;
            var swapchainHeight = viewConfig.RecommendedImageRectHeight;
            var swapchainCreateInfo = new SwapchainCreateInfo
            {
                Type = StructureType.SwapchainCreateInfo,
                CreateFlags = 0,
                UsageFlags =
                    SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit,
                Format = _colorSwapchainFormat,
                SampleCount = viewConfig.RecommendedSwapchainSampleCount,
                Width = swapchainWidth,
                Height = swapchainHeight,
                FaceCount = 1,
                ArraySize = 1,
                MipCount = 1,
            };
            var createSwapchainResult = _xr.CreateSwapchain(
                _session,
                ref swapchainCreateInfo,
                ref _colorSwapchains[eye]
            );
            if (createSwapchainResult != Result.Success)
            {
                return createSwapchainResult;
            }

            uint imageCount = 0;
            var enumerateImagesResult = _xr.EnumerateSwapchainImages(
                _colorSwapchains[eye],
                0,
                ref imageCount,
                (SwapchainImageBaseHeader*)0
            );
            if (enumerateImagesResult != Result.Success)
            {
                return enumerateImagesResult;
            }

            var images = new SwapchainImageD3D11KHR[imageCount];
            for (var i = 0; i < images.Length; i++)
            {
                images[i].Type = StructureType.SwapchainImageD3D11Khr;
            }

            fixed (SwapchainImageD3D11KHR* imagesPointer = images)
            {
                enumerateImagesResult = _xr.EnumerateSwapchainImages(
                    _colorSwapchains[eye],
                    imageCount,
                    ref imageCount,
                    (SwapchainImageBaseHeader*)imagesPointer
                );
                if (enumerateImagesResult != Result.Success)
                {
                    return enumerateImagesResult;
                }
            }

            _swapchainRenderWidths[eye] = swapchainWidth;
            _swapchainRenderHeights[eye] = swapchainHeight;
            _swapchainImages[eye] = images;
        }

        const uint placeholderWidth = 1600;
        const uint placeholderHeight = 900;
        var placeholderSwapchainCreateInfo = new SwapchainCreateInfo
        {
            Type = StructureType.SwapchainCreateInfo,
            CreateFlags = 0,
            UsageFlags = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit,
            Format = _colorSwapchainFormat,
            SampleCount = 1,
            Width = placeholderWidth,
            Height = placeholderHeight,
            FaceCount = 1,
            ArraySize = 1,
            MipCount = 1,
        };
        var createPlaceholderSwapchainResult = _xr.CreateSwapchain(
            _session,
            ref placeholderSwapchainCreateInfo,
            ref _placeholderQuadSwapchain
        );
        if (createPlaceholderSwapchainResult != Result.Success)
        {
            return createPlaceholderSwapchainResult;
        }

        uint placeholderImageCount = 0;
        var enumeratePlaceholderImagesResult = _xr.EnumerateSwapchainImages(
            _placeholderQuadSwapchain,
            0,
            ref placeholderImageCount,
            (SwapchainImageBaseHeader*)0
        );
        if (enumeratePlaceholderImagesResult != Result.Success)
        {
            return enumeratePlaceholderImagesResult;
        }

        var placeholderImages = new SwapchainImageD3D11KHR[placeholderImageCount];
        for (var i = 0; i < placeholderImages.Length; i++)
        {
            placeholderImages[i].Type = StructureType.SwapchainImageD3D11Khr;
        }

        fixed (SwapchainImageD3D11KHR* placeholderImagesPointer = placeholderImages)
        {
            enumeratePlaceholderImagesResult = _xr.EnumerateSwapchainImages(
                _placeholderQuadSwapchain,
                placeholderImageCount,
                ref placeholderImageCount,
                (SwapchainImageBaseHeader*)placeholderImagesPointer
            );
            if (enumeratePlaceholderImagesResult != Result.Success)
            {
                return enumeratePlaceholderImagesResult;
            }
        }

        _placeholderQuadSwapchainImages = placeholderImages;
        _placeholderQuadSwapchainWidth = placeholderWidth;
        _placeholderQuadSwapchainHeight = placeholderHeight;

        return Result.Success;
    }

    private void DestroyStereoRendering()
    {
        if (_xr is null)
        {
            return;
        }

        for (var eye = 0; eye < StereoViewCount; eye++)
        {
            if (_colorSwapchains[eye].Handle != 0)
            {
                _xr.DestroySwapchain(_colorSwapchains[eye]);
                _colorSwapchains[eye] = default;
            }

            _swapchainImages[eye] = [];
        }

        if (_placeholderQuadSwapchain.Handle != 0)
        {
            _xr.DestroySwapchain(_placeholderQuadSwapchain);
            _placeholderQuadSwapchain = default;
        }

        _placeholderQuadSwapchainImages = [];
        _placeholderQuadSwapchainWidth = 0;
        _placeholderQuadSwapchainHeight = 0;

        ReleaseVideoProcessorResources();
    }

    private static string DescribeSwapchainFormat(long format)
    {
        if (format == DxgiFormatB8G8R8A8UnormSrgb)
        {
            return "B8G8R8A8_UNORM_SRGB";
        }

        if (format == DxgiFormatB8G8R8A8Unorm)
        {
            return "B8G8R8A8_UNORM";
        }

        if (format == DxgiFormatR8G8B8A8UnormSrgb)
        {
            return "R8G8B8A8_UNORM_SRGB";
        }

        if (format == DxgiFormatR8G8B8A8Unorm)
        {
            return "R8G8B8A8_UNORM";
        }

        if (format == DxgiFormatNv12)
        {
            return "NV12";
        }

        return format.ToString();
    }

    private bool TrySelectSwapchainFormatForNv12VideoProcessor(
        IReadOnlyCollection<long> availableFormats,
        out long selectedSwapchainFormat,
        out string probeSummary
    )
    {
        selectedSwapchainFormat = 0;
        var candidateFormats = availableFormats
            .Where(static format => IsSupportedColorSwapchainFormat(format))
            .Distinct()
            .ToList();

        if (candidateFormats.Count == 0)
        {
            probeSummary = "vpProbe=no-rgba-bgra-candidate";
            return false;
        }

        candidateFormats = SortFormatsByUserPreference(
            candidateFormats,
            _requestedSwapchainFormatLabel
        );
        var usageCandidates = new[] { VideoUsage.OptimalSpeed, (VideoUsage)0, (VideoUsage)2 };
        foreach (var usage in usageCandidates.Distinct())
        {
            if (
                !TryProbeNv12VideoProcessorOutputSupport(
                    1920,
                    1080,
                    1080,
                    usage,
                    out var nv12InputSupported,
                    out var rgbaOutputSupported,
                    out var bgraOutputSupported
                )
            )
            {
                continue;
            }

            if (!nv12InputSupported)
            {
                continue;
            }

            foreach (var candidateFormat in candidateFormats)
            {
                var outputSupported = IsRgbaFamilyFormat(candidateFormat)
                    ? rgbaOutputSupported
                    : bgraOutputSupported;
                if (!outputSupported)
                {
                    continue;
                }

                selectedSwapchainFormat = candidateFormat;
                _videoProcessorUsage = usage;
                probeSummary =
                    $"vpProbe=ok usage={(int)usage} selected={DescribeSwapchainFormat(candidateFormat)} "
                    + $"nv12In={nv12InputSupported} rgbaOut={rgbaOutputSupported} bgraOut={bgraOutputSupported}";
                return true;
            }
        }

        probeSummary = "vpProbe=unsupported nv12->rgba/bgra";
        return false;
    }

    private bool TryProbeNv12VideoProcessorOutputSupport(
        uint inputWidth,
        uint inputHeight,
        uint outputHeight,
        VideoUsage usage,
        out bool nv12InputSupported,
        out bool rgbaOutputSupported,
        out bool bgraOutputSupported
    )
    {
        nv12InputSupported = false;
        rgbaOutputSupported = false;
        bgraOutputSupported = false;
        if (_d3d11Device is null)
        {
            return false;
        }

        ID3D11VideoDevice* videoDevice = null;
        ID3D11VideoProcessorEnumerator* enumerator = null;
        try
        {
            void* videoDevicePointer = null;
            var videoDeviceGuid = Id3D11VideoDeviceGuid;
            if (
                _d3d11Device->QueryInterface(ref videoDeviceGuid, ref videoDevicePointer) < 0
                || videoDevicePointer is null
            )
            {
                return false;
            }

            videoDevice = (ID3D11VideoDevice*)videoDevicePointer;
            var contentDesc = new VideoProcessorContentDesc
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                InputWidth = inputWidth,
                InputHeight = inputHeight,
                OutputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                OutputWidth = inputWidth,
                OutputHeight = outputHeight,
                Usage = usage,
            };
            if (
                videoDevice->CreateVideoProcessorEnumerator(ref contentDesc, ref enumerator) < 0
                || enumerator is null
            )
            {
                return false;
            }

            uint sourceSupport = 0;
            if (enumerator->CheckVideoProcessorFormat(Format.FormatNV12, ref sourceSupport) >= 0)
            {
                nv12InputSupported =
                    ((VideoProcessorFormatSupport)sourceSupport & VideoProcessorFormatSupport.Input)
                    != 0;
            }

            uint rgbaSupport = 0;
            if (
                enumerator->CheckVideoProcessorFormat(Format.FormatR8G8B8A8Unorm, ref rgbaSupport)
                >= 0
            )
            {
                rgbaOutputSupported =
                    ((VideoProcessorFormatSupport)rgbaSupport & VideoProcessorFormatSupport.Output)
                    != 0;
            }

            uint bgraSupport = 0;
            if (
                enumerator->CheckVideoProcessorFormat(Format.FormatB8G8R8A8Unorm, ref bgraSupport)
                >= 0
            )
            {
                bgraOutputSupported =
                    ((VideoProcessorFormatSupport)bgraSupport & VideoProcessorFormatSupport.Output)
                    != 0;
            }

            return true;
        }
        finally
        {
            if (enumerator is not null)
            {
                _ = enumerator->Release();
            }

            if (videoDevice is not null)
            {
                _ = videoDevice->Release();
            }
        }
    }

    private static List<long> SortFormatsByUserPreference(
        List<long> formats,
        string requestedSwapchainFormat
    )
    {
        var requested = NormalizePreferredSwapchainFormat(requestedSwapchainFormat);
        if (requested.Equals("RGBA8", StringComparison.OrdinalIgnoreCase))
        {
            return formats.Where(static format => IsRgbaFamilyFormat(format)).ToList();
        }

        if (requested.Equals("BGRA8", StringComparison.OrdinalIgnoreCase))
        {
            return formats.Where(static format => IsBgraFamilyFormat(format)).ToList();
        }

        return formats.OrderBy(static format => GetAutoSwapchainFormatPriority(format)).ToList();
    }

    private static string NormalizePreferredSwapchainFormat(string? preferredSwapchainFormat)
    {
        if (string.IsNullOrWhiteSpace(preferredSwapchainFormat))
        {
            return "Auto";
        }

        var normalized = preferredSwapchainFormat.Trim();
        if (
            normalized.Equals("R8G8B8A8_UNORM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("R8G8B8A8_UNORM_SRGB", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("RGBA8", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "RGBA8";
        }

        if (
            normalized.Equals("B8G8R8A8_UNORM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("B8G8R8A8_UNORM_SRGB", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("BGRA8", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "BGRA8";
        }

        return "Auto";
    }

    private static string ToUiSwapchainLabel(long format)
    {
        if (IsRgbaFamilyFormat(format))
        {
            return "RGBA8";
        }

        if (IsBgraFamilyFormat(format))
        {
            return "BGRA8";
        }

        return DescribeSwapchainFormat(format);
    }

    private static bool IsSupportedColorSwapchainFormat(long format)
    {
        return IsBgraFamilyFormat(format) || IsRgbaFamilyFormat(format);
    }

    private static bool IsBgraFamilyFormat(long format)
    {
        return format == DxgiFormatB8G8R8A8Unorm || format == DxgiFormatB8G8R8A8UnormSrgb;
    }

    private static bool IsRgbaFamilyFormat(long format)
    {
        return format == DxgiFormatR8G8B8A8Unorm || format == DxgiFormatR8G8B8A8UnormSrgb;
    }

    private static int GetAutoSwapchainFormatPriority(long format)
    {
        if (format == DxgiFormatB8G8R8A8UnormSrgb)
        {
            return 0;
        }

        if (format == DxgiFormatB8G8R8A8Unorm)
        {
            return 1;
        }

        if (format == DxgiFormatR8G8B8A8UnormSrgb)
        {
            return 2;
        }

        if (format == DxgiFormatR8G8B8A8Unorm)
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static string NormalizePreferredGraphicsAdapter(string? preferredGraphicsAdapter)
    {
        if (string.IsNullOrWhiteSpace(preferredGraphicsAdapter))
        {
            return "Auto";
        }

        var normalized = preferredGraphicsAdapter.Trim();
        return normalized;
    }

    private static string NormalizePreferredGraphicsBackend(string? preferredGraphicsBackend)
    {
        if (string.IsNullOrWhiteSpace(preferredGraphicsBackend))
        {
            return "D3D11";
        }

        var normalized = preferredGraphicsBackend.Trim();
        if (normalized.Equals("D3D11", StringComparison.OrdinalIgnoreCase))
        {
            return "D3D11";
        }

        return "D3D11";
    }
}
