using LLMeta.App.Models;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private Result RenderStereoProjectionLayer(long predictedDisplayTime, out bool rendered)
    {
        rendered = false;
        if (_xr is null || _localSpace.Handle == 0)
        {
            return Result.ErrorHandleInvalid;
        }

        var viewLocateInfo = new ViewLocateInfo
        {
            Type = StructureType.ViewLocateInfo,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo,
            DisplayTime = predictedDisplayTime,
            Space = _localSpace,
        };
        var viewState = new ViewState { Type = StructureType.ViewState };
        uint viewCountOutput = 0;
        Result locateViewsResult;
        fixed (View* viewsPointer = _views)
        {
            locateViewsResult = _xr.LocateView(
                _session,
                ref viewLocateInfo,
                ref viewState,
                StereoViewCount,
                ref viewCountOutput,
                viewsPointer
            );
        }

        if (locateViewsResult != Result.Success)
        {
            return locateViewsResult;
        }

        if (viewCountOutput < StereoViewCount)
        {
            return Result.ErrorValidationFailure;
        }

        var projectionViews = new CompositionLayerProjectionView[StereoViewCount];

        for (var eye = 0; eye < StereoViewCount; eye++)
        {
            var acquireInfo = new SwapchainImageAcquireInfo
            {
                Type = StructureType.SwapchainImageAcquireInfo,
            };
            uint imageIndex = 0;
            var acquireResult = _xr.AcquireSwapchainImage(
                _colorSwapchains[eye],
                ref acquireInfo,
                ref imageIndex
            );
            if (acquireResult != Result.Success)
            {
                return acquireResult;
            }

            var waitInfo = new SwapchainImageWaitInfo
            {
                Type = StructureType.SwapchainImageWaitInfo,
                Timeout = long.MaxValue,
            };
            var waitResult = _xr.WaitSwapchainImage(_colorSwapchains[eye], ref waitInfo);
            if (waitResult != Result.Success)
            {
                return waitResult;
            }

            var targetTexture = (ID3D11Texture2D*)_swapchainImages[eye][imageIndex].Texture;
            UploadEyeTexture(targetTexture, eye);

            var releaseInfo = new SwapchainImageReleaseInfo
            {
                Type = StructureType.SwapchainImageReleaseInfo,
            };
            var releaseResult = _xr.ReleaseSwapchainImage(_colorSwapchains[eye], ref releaseInfo);
            if (releaseResult != Result.Success)
            {
                return releaseResult;
            }

            projectionViews[eye] = new CompositionLayerProjectionView
            {
                Type = StructureType.CompositionLayerProjectionView,
                Pose = _views[eye].Pose,
                Fov = _views[eye].Fov,
                SubImage = new SwapchainSubImage
                {
                    Swapchain = _colorSwapchains[eye],
                    ImageRect = new Rect2Di
                    {
                        Offset = new Offset2Di { X = 0, Y = 0 },
                        Extent = new Extent2Di
                        {
                            Width = (int)_viewConfigurationViews[eye].RecommendedImageRectWidth,
                            Height = (int)_viewConfigurationViews[eye].RecommendedImageRectHeight,
                        },
                    },
                    ImageArrayIndex = 0,
                },
            };
        }

        fixed (CompositionLayerProjectionView* projectionViewsPointer = projectionViews)
        {
            var projectionLayer = new CompositionLayerProjection
            {
                Type = StructureType.CompositionLayerProjection,
                Space = _localSpace,
                ViewCount = StereoViewCount,
                Views = projectionViewsPointer,
            };
            var layerHeader = (CompositionLayerBaseHeader*)(&projectionLayer);
            CompositionLayerBaseHeader*[] layerHeaders = [layerHeader];
            fixed (CompositionLayerBaseHeader** layerHeadersPointer = layerHeaders)
            {
                var endInfo = new FrameEndInfo
                {
                    Type = StructureType.FrameEndInfo,
                    DisplayTime = predictedDisplayTime,
                    EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                    LayerCount = 1,
                    Layers = layerHeadersPointer,
                };
                var endResult = _xr.EndFrame(_session, ref endInfo);
                rendered = endResult == Result.Success;
                return endResult;
            }
        }
    }

    private void UpdateLatestSbsFrame(DecodedVideoFrame frame)
    {
        lock (_videoFrameLock)
        {
            _latestSbsBgra = frame.BgraPixels;
            _latestSbsWidth = frame.Width;
            _latestSbsHeight = frame.Height;
            _latestSbsVisibleHeight = ResolveVisibleHeight(frame.Width, frame.Height);
            _latestVideoSequence = frame.Sequence;
        }
    }

    private void UploadEyeTexture(ID3D11Texture2D* texture, int eye)
    {
        if (_d3d11DeviceContext is null)
        {
            return;
        }

        byte[]? latest;
        int sourceWidth;
        int sourceHeight;
        int sourceVisibleHeight;
        lock (_videoFrameLock)
        {
            latest = _latestSbsBgra;
            sourceWidth = _latestSbsWidth;
            sourceHeight = _latestSbsHeight;
            sourceVisibleHeight =
                _latestSbsVisibleHeight > 0 ? _latestSbsVisibleHeight : _latestSbsHeight;
        }

        var targetWidth = (int)_viewConfigurationViews[eye].RecommendedImageRectWidth;
        var targetHeight = (int)_viewConfigurationViews[eye].RecommendedImageRectHeight;
        var eyePixels = EnsureEyeScratchBuffer(eye, targetWidth, targetHeight);
        if (latest is not null && sourceWidth > 1 && sourceHeight > 0 && sourceVisibleHeight > 0)
        {
            CopySbsHalfToTarget(
                latest,
                sourceWidth,
                sourceVisibleHeight,
                eye,
                eyePixels,
                targetWidth,
                targetHeight
            );
        }
        else
        {
            Array.Clear(eyePixels, 0, eyePixels.Length);
        }

        fixed (byte* sourcePointer = eyePixels)
        {
            _d3d11DeviceContext->UpdateSubresource(
                (ID3D11Resource*)texture,
                0,
                (Box*)0,
                sourcePointer,
                (uint)(targetWidth * 4),
                0
            );
        }
    }

    private void CopySbsHalfToTarget(
        byte[] sourceBgra,
        int sourceWidth,
        int sourceVisibleHeight,
        int eye,
        byte[] targetBgra,
        int targetWidth,
        int targetHeight
    )
    {
        var halfWidth = sourceWidth / 2;
        var srcStartX = eye == 0 ? 0 : halfWidth;
        var sourceHeight = sourceVisibleHeight;
        if (halfWidth <= 0 || sourceHeight <= 0)
        {
            return;
        }

        var (xMap, yMap) = EnsureSamplingMaps(
            eye,
            sourceWidth,
            sourceHeight,
            targetWidth,
            targetHeight
        );
        for (var y = 0; y < targetHeight; y++)
        {
            var srcY = yMap[y];
            var srcRowStart = srcY * sourceWidth * 4;
            var dstRowStart = y * targetWidth * 4;
            for (var x = 0; x < targetWidth; x++)
            {
                var srcX = srcStartX + xMap[x];
                var srcIndex = srcRowStart + (srcX * 4);
                var dstIndex = dstRowStart + (x * 4);
                targetBgra[dstIndex] = sourceBgra[srcIndex];
                targetBgra[dstIndex + 1] = sourceBgra[srcIndex + 1];
                targetBgra[dstIndex + 2] = sourceBgra[srcIndex + 2];
                targetBgra[dstIndex + 3] = 255;
            }
        }
    }

    private (int[] xMap, int[] yMap) EnsureSamplingMaps(
        int eye,
        int sourceWidth,
        int sourceVisibleHeight,
        int targetWidth,
        int targetHeight
    )
    {
        if (
            _eyeSampleXMaps[eye] is not null
            && _eyeSampleYMaps[eye] is not null
            && _eyeMapSourceWidths[eye] == sourceWidth
            && _eyeMapSourceHeights[eye] == sourceVisibleHeight
            && _eyeMapTargetWidths[eye] == targetWidth
            && _eyeMapTargetHeights[eye] == targetHeight
        )
        {
            return (_eyeSampleXMaps[eye]!, _eyeSampleYMaps[eye]!);
        }

        var halfWidth = sourceWidth / 2;
        var xMap = new int[targetWidth];
        for (var x = 0; x < targetWidth; x++)
        {
            xMap[x] = x * halfWidth / targetWidth;
        }

        var yMap = new int[targetHeight];
        for (var y = 0; y < targetHeight; y++)
        {
            yMap[y] = y * sourceVisibleHeight / targetHeight;
        }

        _eyeSampleXMaps[eye] = xMap;
        _eyeSampleYMaps[eye] = yMap;
        _eyeMapSourceWidths[eye] = sourceWidth;
        _eyeMapSourceHeights[eye] = sourceVisibleHeight;
        _eyeMapTargetWidths[eye] = targetWidth;
        _eyeMapTargetHeights[eye] = targetHeight;
        return (xMap, yMap);
    }

    private byte[] EnsureEyeScratchBuffer(int eye, int width, int height)
    {
        var requiredLength = checked(width * height * 4);
        if (
            _eyeScratchBuffers[eye] is null
            || _eyeScratchBuffers[eye].Length != requiredLength
            || _eyeScratchWidths[eye] != width
            || _eyeScratchHeights[eye] != height
        )
        {
            _eyeScratchBuffers[eye] = new byte[requiredLength];
            _eyeScratchWidths[eye] = width;
            _eyeScratchHeights[eye] = height;
        }

        return _eyeScratchBuffers[eye];
    }

    private static int ResolveVisibleHeight(int width, int height)
    {
        if (width == 1920 && height == 1088)
        {
            return 1080;
        }

        return height;
    }
}
