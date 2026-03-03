using LLMeta.App.Models;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private const int StereoViewCount = 2;
    private const long DxgiFormatB8G8R8A8Unorm = 87;

    private readonly object _videoFrameLock = new();
    private readonly Swapchain[] _colorSwapchains = new Swapchain[StereoViewCount];
    private readonly SwapchainImageD3D11KHR[][] _swapchainImages = new SwapchainImageD3D11KHR[
        StereoViewCount
    ][];
    private readonly ViewConfigurationView[] _viewConfigurationViews = new ViewConfigurationView[
        StereoViewCount
    ];
    private readonly View[] _views = new View[StereoViewCount];
    private readonly byte[][] _eyeScratchBuffers = new byte[StereoViewCount][];
    private readonly int[] _eyeScratchWidths = new int[StereoViewCount];
    private readonly int[] _eyeScratchHeights = new int[StereoViewCount];
    private readonly int[][] _eyeSampleXMaps = new int[StereoViewCount][];
    private readonly int[][] _eyeSampleYMaps = new int[StereoViewCount][];
    private readonly int[] _eyeMapSourceWidths = new int[StereoViewCount];
    private readonly int[] _eyeMapSourceHeights = new int[StereoViewCount];
    private readonly int[] _eyeMapTargetWidths = new int[StereoViewCount];
    private readonly int[] _eyeMapTargetHeights = new int[StereoViewCount];

    private byte[]? _latestSbsBgra;
    private int _latestSbsWidth;
    private int _latestSbsHeight;
    private int _latestSbsVisibleHeight;
    private uint _latestVideoSequence;

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

        var bgraSupported = false;
        foreach (var format in formats)
        {
            if (format == DxgiFormatB8G8R8A8Unorm)
            {
                bgraSupported = true;
                break;
            }
        }

        if (!bgraSupported)
        {
            return Result.ErrorSwapchainFormatUnsupported;
        }

        for (var eye = 0; eye < StereoViewCount; eye++)
        {
            var viewConfig = _viewConfigurationViews[eye];
            var swapchainCreateInfo = new SwapchainCreateInfo
            {
                Type = StructureType.SwapchainCreateInfo,
                CreateFlags = 0,
                UsageFlags =
                    SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit,
                Format = DxgiFormatB8G8R8A8Unorm,
                SampleCount = viewConfig.RecommendedSwapchainSampleCount,
                Width = viewConfig.RecommendedImageRectWidth,
                Height = viewConfig.RecommendedImageRectHeight,
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

            _swapchainImages[eye] = images;
        }

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
    }
}
