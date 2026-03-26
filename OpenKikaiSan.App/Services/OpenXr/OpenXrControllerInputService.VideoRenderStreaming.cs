using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenKikaiSan.App.Models;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.OpenXR;

namespace OpenKikaiSan.App.Services;

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
        var renderedSequence = 0u;
        var renderedAgeFromReceiveMs = 0L;
        var renderedAgeFromDecodeMs = 0L;
        var firstUploadFailureCode = 0;
        var hasRenderedVideoFrame = false;
        var sourceFrameSnapshot = AcquireLatestSbsFrameSnapshot();

        try
        {
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
                var eyeHasFrame = UploadEyeTexture(
                    targetTexture,
                    eye,
                    sourceFrameSnapshot,
                    out var sequence,
                    out var ageFromReceiveMs,
                    out var ageFromDecodeMs,
                    out var uploadFailureCode
                );
                if (eyeHasFrame && !hasRenderedVideoFrame)
                {
                    hasRenderedVideoFrame = true;
                    renderedSequence = sequence;
                    renderedAgeFromReceiveMs = ageFromReceiveMs;
                    renderedAgeFromDecodeMs = ageFromDecodeMs;
                }
                else if (!eyeHasFrame && firstUploadFailureCode == 0 && uploadFailureCode != 0)
                {
                    firstUploadFailureCode = uploadFailureCode;
                }

                var releaseInfo = new SwapchainImageReleaseInfo
                {
                    Type = StructureType.SwapchainImageReleaseInfo,
                };
                var releaseResult = _xr.ReleaseSwapchainImage(
                    _colorSwapchains[eye],
                    ref releaseInfo
                );
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
                                Width = (int)_swapchainRenderWidths[eye],
                                Height = (int)_swapchainRenderHeights[eye],
                            },
                        },
                        ImageArrayIndex = 0,
                    },
                };
            }
        }
        finally
        {
            ReleaseVideoFrameSnapshot(sourceFrameSnapshot);
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
            var projectionLayerHeader = (CompositionLayerBaseHeader*)(&projectionLayer);
            CompositionLayerQuad placeholderQuadLayer = default;
            CompositionLayerBaseHeader*[] layerHeaders;
            if (hasRenderedVideoFrame || !TryBuildPlaceholderQuadLayer(out placeholderQuadLayer))
            {
                layerHeaders = [projectionLayerHeader];
            }
            else
            {
                var placeholderQuadLayerHeader = (CompositionLayerBaseHeader*)(
                    &placeholderQuadLayer
                );
                layerHeaders = [projectionLayerHeader, placeholderQuadLayerHeader];
            }
            fixed (CompositionLayerBaseHeader** layerHeadersPointer = layerHeaders)
            {
                var endInfo = new FrameEndInfo
                {
                    Type = StructureType.FrameEndInfo,
                    DisplayTime = predictedDisplayTime,
                    EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                    LayerCount = (uint)layerHeaders.Length,
                    Layers = layerHeadersPointer,
                };
                var endResult = _xr.EndFrame(_session, ref endInfo);
                rendered = endResult == Result.Success;
                if (rendered && hasRenderedVideoFrame)
                {
                    lock (_videoFrameLock)
                    {
                        _videoRenderStats = new OpenXrVideoRenderStats(
                            renderedSequence,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            renderedAgeFromReceiveMs,
                            renderedAgeFromDecodeMs,
                            0,
                            0
                        );
                    }
                }
                else if (rendered)
                {
                    var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    lock (_videoFrameLock)
                    {
                        _videoRenderStats = new OpenXrVideoRenderStats(
                            _videoRenderStats.LastRenderedSequence,
                            _videoRenderStats.LastRenderedAtUnixMs,
                            _videoRenderStats.LastRenderedAgeFromReceiveMs,
                            _videoRenderStats.LastRenderedAgeFromDecodeMs,
                            firstUploadFailureCode,
                            nowUnixMs
                        );
                    }
                }
                return endResult;
            }
        }
    }

    private void UpdateLatestSbsFrame(DecodedVideoFrame frame)
    {
        if (frame.SourceTexturePointer == IntPtr.Zero)
        {
            return;
        }

        lock (_videoFrameLock)
        {
            if (_latestSbsSourceTexturePointer != IntPtr.Zero)
            {
                Marshal.Release(_latestSbsSourceTexturePointer);
            }

            _latestSbsSourceTexturePointer = frame.SourceTexturePointer;
            _latestSbsSourceSubresourceIndex = frame.SourceSubresourceIndex;
            _latestSbsTimestampUnixMs = frame.TimestampUnixMs;
            _latestSbsDecodedUnixMs = frame.DecodedUnixMs;
            _latestSbsWidth = frame.Width;
            _latestSbsHeight = frame.Height;
            _latestSbsVisibleHeight = ResolveVisibleHeight(frame.Width, frame.Height);
            _latestVideoSequence = frame.Sequence;
        }
    }

    private bool UploadEyeTexture(
        ID3D11Texture2D* texture,
        int eye,
        VideoFrameSnapshot sourceFrameSnapshot,
        out uint sequence,
        out long ageFromReceiveMs,
        out long ageFromDecodeMs,
        out int uploadFailureCode
    )
    {
        sequence = 0;
        ageFromReceiveMs = 0;
        ageFromDecodeMs = 0;
        uploadFailureCode = 0;
        if (_d3d11DeviceContext is null)
        {
            uploadFailureCode = 1;
            return false;
        }

        var sourceTexturePointer = sourceFrameSnapshot.SourceTexturePointer;
        var sourceSubresourceIndex = sourceFrameSnapshot.SourceSubresourceIndex;
        var sourceTimestampUnixMs = sourceFrameSnapshot.SourceTimestampUnixMs;
        var sourceDecodedUnixMs = sourceFrameSnapshot.SourceDecodedUnixMs;
        var sourceWidth = sourceFrameSnapshot.SourceWidth;
        var sourceVisibleHeight = sourceFrameSnapshot.SourceVisibleHeight;
        sequence = sourceFrameSnapshot.Sequence;

        if (sourceTexturePointer == IntPtr.Zero || sourceWidth <= 1 || sourceVisibleHeight <= 0)
        {
            ClearEyeTextureToBlack(texture);
            uploadFailureCode = 2;
            return false;
        }

        var sourceTexture = (ID3D11Texture2D*)sourceTexturePointer;
        Texture2DDesc sourceTextureDesc = default;
        Texture2DDesc targetTextureDesc = default;
        sourceTexture->GetDesc(&sourceTextureDesc);
        texture->GetDesc(&targetTextureDesc);
        var halfWidth = sourceWidth / 2;
        if (halfWidth <= 0)
        {
            uploadFailureCode = 4;
            return false;
        }

        var requiresScaledBlit =
            sourceTextureDesc.Format == Format.FormatNV12
            && (
                sourceTextureDesc.Format != targetTextureDesc.Format
                || halfWidth != (int)targetTextureDesc.Width
                || sourceVisibleHeight != (int)targetTextureDesc.Height
            );
        if (requiresScaledBlit)
        {
            if (
                !TryBlitEyeTextureWithVideoProcessor(
                    sourceTexture,
                    sourceSubresourceIndex,
                    sourceTextureDesc,
                    texture,
                    targetTextureDesc,
                    eye,
                    halfWidth,
                    sourceVisibleHeight,
                    out uploadFailureCode
                )
            )
            {
                return false;
            }

            var nowUnixMsScaled = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ageFromReceiveMs =
                sourceTimestampUnixMs == 0 ? 0 : nowUnixMsScaled - (long)sourceTimestampUnixMs;
            ageFromDecodeMs =
                sourceDecodedUnixMs == 0 ? 0 : nowUnixMsScaled - (long)sourceDecodedUnixMs;
            if (ageFromReceiveMs < 0)
            {
                ageFromReceiveMs = 0;
            }

            if (ageFromDecodeMs < 0)
            {
                ageFromDecodeMs = 0;
            }

            return true;
        }

        var copySourceTexture = sourceTexture;
        var copySourceSubresourceIndex = sourceSubresourceIndex;
        if (!CanCopyWithoutFormatConversion(sourceTextureDesc.Format, targetTextureDesc.Format))
        {
            if (
                !TryConvertSourceTextureToSwapchainFormat(
                    sourceTexture,
                    sourceSubresourceIndex,
                    sourceTextureDesc,
                    sourceTextureDesc.Height,
                    targetTextureDesc.Format,
                    out copySourceTexture,
                    out var convertFailureCode
                )
            )
            {
                uploadFailureCode = convertFailureCode;
                return false;
            }

            copySourceSubresourceIndex = 0;
        }

        var copyWidth = Math.Min(halfWidth, (int)targetTextureDesc.Width);
        var copyHeight = Math.Min(sourceVisibleHeight, (int)targetTextureDesc.Height);
        if (copyWidth <= 0 || copyHeight <= 0)
        {
            uploadFailureCode = 48;
            return false;
        }

        if (copyWidth < (int)targetTextureDesc.Width || copyHeight < (int)targetTextureDesc.Height)
        {
            ClearEyeTextureToBlack(texture);
        }

        var sourceLeft = (uint)(eye == 0 ? 0 : halfWidth);
        var sourceRight = (uint)(sourceLeft + copyWidth);
        var sourceBottom = (uint)copyHeight;
        var sourceBox = new Box
        {
            Left = sourceLeft,
            Top = 0,
            Front = 0,
            Right = sourceRight,
            Bottom = sourceBottom,
            Back = 1,
        };

        _d3d11DeviceContext->CopySubresourceRegion(
            (ID3D11Resource*)texture,
            0,
            0,
            0,
            0,
            (ID3D11Resource*)copySourceTexture,
            copySourceSubresourceIndex,
            &sourceBox
        );
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ageFromReceiveMs = sourceTimestampUnixMs == 0 ? 0 : nowUnixMs - (long)sourceTimestampUnixMs;
        ageFromDecodeMs = sourceDecodedUnixMs == 0 ? 0 : nowUnixMs - (long)sourceDecodedUnixMs;
        if (ageFromReceiveMs < 0)
        {
            ageFromReceiveMs = 0;
        }

        if (ageFromDecodeMs < 0)
        {
            ageFromDecodeMs = 0;
        }

        return true;
    }

    private VideoFrameSnapshot AcquireLatestSbsFrameSnapshot()
    {
        lock (_videoFrameLock)
        {
            if (_latestSbsSourceTexturePointer != IntPtr.Zero)
            {
                Marshal.AddRef(_latestSbsSourceTexturePointer);
            }

            return new VideoFrameSnapshot(
                _latestSbsSourceTexturePointer,
                _latestSbsSourceSubresourceIndex,
                _latestSbsTimestampUnixMs,
                _latestSbsDecodedUnixMs,
                _latestSbsWidth,
                _latestSbsVisibleHeight > 0 ? _latestSbsVisibleHeight : _latestSbsHeight,
                _latestVideoSequence
            );
        }
    }

    private static void ReleaseVideoFrameSnapshot(VideoFrameSnapshot snapshot)
    {
        if (snapshot.SourceTexturePointer != IntPtr.Zero)
        {
            Marshal.Release(snapshot.SourceTexturePointer);
        }
    }

    private bool TryBlitEyeTextureWithVideoProcessor(
        ID3D11Texture2D* sourceTexture,
        uint sourceSubresourceIndex,
        Texture2DDesc sourceTextureDesc,
        ID3D11Texture2D* targetTexture,
        Texture2DDesc targetTextureDesc,
        int eye,
        int halfWidth,
        int sourceVisibleHeight,
        out int failureCode
    )
    {
        failureCode = 49;
        if (
            !EnsureVideoProcessorForTargetBlit(
                sourceTexture,
                sourceSubresourceIndex,
                sourceTextureDesc,
                targetTextureDesc.Width,
                targetTextureDesc.Height,
                targetTextureDesc.Format,
                out failureCode
            )
        )
        {
            return false;
        }

        if (
            _videoProcessor is null
            || _videoProcessorOutputView is null
            || _videoProcessorOutputTexture is null
            || _videoProcessorInputView is null
            || _d3d11VideoContext is null
        )
        {
            failureCode = 50;
            return false;
        }

        var sourceLeft = eye == 0 ? 0 : halfWidth;
        var sourceRect = new Box2D<int>(sourceLeft, 0, sourceLeft + halfWidth, sourceVisibleHeight);
        var destinationRect = new Box2D<int>(
            0,
            0,
            (int)targetTextureDesc.Width,
            (int)targetTextureDesc.Height
        );

        _d3d11VideoContext->VideoProcessorSetStreamFrameFormat(
            _videoProcessor,
            0,
            VideoFrameFormat.Progressive
        );
        _d3d11VideoContext->VideoProcessorSetStreamAutoProcessingMode(_videoProcessor, 0, 0);
        _d3d11VideoContext->VideoProcessorSetStreamSourceRect(
            _videoProcessor,
            0,
            1,
            ref sourceRect
        );
        _d3d11VideoContext->VideoProcessorSetStreamDestRect(
            _videoProcessor,
            0,
            1,
            ref destinationRect
        );
        _d3d11VideoContext->VideoProcessorSetOutputTargetRect(
            _videoProcessor,
            1,
            ref destinationRect
        );

        var stream = new VideoProcessorStream
        {
            Enable = 1,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            PpPastSurfaces = null,
            PInputSurface = _videoProcessorInputView,
            PpFutureSurfaces = null,
            PpPastSurfacesRight = null,
            PInputSurfaceRight = null,
            PpFutureSurfacesRight = null,
        };

        var blitHr = _d3d11VideoContext->VideoProcessorBlt(
            _videoProcessor,
            _videoProcessorOutputView,
            0,
            1,
            &stream
        );
        if (blitHr < 0)
        {
            failureCode = 52;
            return false;
        }

        _d3d11DeviceContext->CopySubresourceRegion(
            (ID3D11Resource*)targetTexture,
            0,
            0,
            0,
            0,
            (ID3D11Resource*)_videoProcessorOutputTexture,
            0,
            (Box*)0
        );

        failureCode = 0;
        return true;
    }

    private void ClearEyeTextureToBlack(ID3D11Texture2D* texture)
    {
        if (_d3d11Device is null || _d3d11DeviceContext is null)
        {
            return;
        }

        Texture2DDesc targetDesc = default;
        texture->GetDesc(&targetDesc);
        if (!EnsureBlackClearTexture(targetDesc.Width, targetDesc.Height, targetDesc.Format))
        {
            return;
        }

        if (_blackClearTexture is null)
        {
            return;
        }

        _d3d11DeviceContext->CopySubresourceRegion(
            (ID3D11Resource*)texture,
            0,
            0,
            0,
            0,
            (ID3D11Resource*)_blackClearTexture,
            0,
            (Box*)0
        );
    }

    private bool EnsureBlackClearTexture(uint width, uint height, Format format)
    {
        if (_d3d11Device is null)
        {
            return false;
        }

        var clearFormat = NormalizeVideoProcessorTargetFormat(format);

        var requiresRecreate =
            _blackClearTexture is null
            || _blackClearTextureWidth != width
            || _blackClearTextureHeight != height
            || _blackClearTextureFormat != clearFormat;
        if (!requiresRecreate)
        {
            return true;
        }

        if (_blackClearTexture is not null)
        {
            _ = _blackClearTexture->Release();
            _blackClearTexture = null;
            _blackClearTextureWidth = 0;
            _blackClearTextureHeight = 0;
            _blackClearTextureFormat = 0;
        }

        using var bitmap = new Bitmap((int)width, (int)height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.DimGray);
        }

        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(
            bounds,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );
        try
        {
            var textureDesc = new Texture2DDesc
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = clearFormat,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage = Usage.Default,
                BindFlags = (uint)BindFlag.None,
                CPUAccessFlags = 0,
                MiscFlags = 0,
            };

            var subresourceData = new SubresourceData
            {
                PSysMem = bitmapData.Scan0.ToPointer(),
                SysMemPitch = (uint)bitmapData.Stride,
                SysMemSlicePitch = 0,
            };

            var createResult = _d3d11Device->CreateTexture2D(
                ref textureDesc,
                ref subresourceData,
                ref _blackClearTexture
            );
            if (createResult < 0 || _blackClearTexture is null)
            {
                _logger?.Debug(
                    $"White clear texture creation failed: hr=0x{createResult:X8} size={width}x{height} fmt={(int)format} normalizedFmt={(int)clearFormat}"
                );
                return false;
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        _blackClearTextureWidth = width;
        _blackClearTextureHeight = height;
        _blackClearTextureFormat = clearFormat;
        return true;
    }

    private bool ShowPlaceholderTexture(ID3D11Texture2D* texture)
    {
        if (_d3d11DeviceContext is null)
        {
            return false;
        }

        Texture2DDesc targetDesc = default;
        texture->GetDesc(&targetDesc);
        if (!EnsurePlaceholderTexture(targetDesc.Width, targetDesc.Height, targetDesc.Format))
        {
            return false;
        }

        if (_placeholderTexture is null)
        {
            return false;
        }

        _d3d11DeviceContext->CopySubresourceRegion(
            (ID3D11Resource*)texture,
            0,
            0,
            0,
            0,
            (ID3D11Resource*)_placeholderTexture,
            0,
            (Box*)0
        );
        return true;
    }

    private bool EnsurePlaceholderTexture(uint width, uint height, Format format)
    {
        if (_d3d11Device is null)
        {
            return false;
        }

        var placeholderFormat = NormalizeVideoProcessorTargetFormat(format);

        var requiresRecreate =
            _placeholderTexture is null
            || _placeholderTextureWidth != width
            || _placeholderTextureHeight != height
            || _placeholderTextureFormat != placeholderFormat;
        if (!requiresRecreate)
        {
            return true;
        }

        if (_placeholderTexture is not null)
        {
            _ = _placeholderTexture->Release();
            _placeholderTexture = null;
            _placeholderTextureWidth = 0;
            _placeholderTextureHeight = 0;
            _placeholderTextureFormat = 0;
        }

        using var bitmap = new Bitmap((int)width, (int)height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.DimGray);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", Math.Max(34.0f, width / 30.0f), FontStyle.Bold);
            using var brush = new SolidBrush(Color.Black);
            using var textFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            var horizontalMargin = width * 0.12f;
            var verticalMargin = height * 0.24f;
            var layout = new RectangleF(
                horizontalMargin,
                verticalMargin,
                width - (horizontalMargin * 2.0f),
                height - (verticalMargin * 2.6f)
            );
            graphics.DrawString(
                "Please select window\nor monitor to capture",
                font,
                brush,
                layout,
                textFormat
            );
        }

        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(
            bounds,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb
        );
        try
        {
            var textureDesc = new Texture2DDesc
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = placeholderFormat,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage = Usage.Default,
                BindFlags = (uint)BindFlag.None,
                CPUAccessFlags = 0,
                MiscFlags = 0,
            };
            var subresourceData = new SubresourceData
            {
                PSysMem = bitmapData.Scan0.ToPointer(),
                SysMemPitch = (uint)bitmapData.Stride,
                SysMemSlicePitch = 0,
            };
            var createResult = _d3d11Device->CreateTexture2D(
                ref textureDesc,
                ref subresourceData,
                ref _placeholderTexture
            );
            if (createResult < 0 || _placeholderTexture is null)
            {
                _logger?.Debug(
                    $"Placeholder texture creation failed: hr=0x{createResult:X8} size={width}x{height} fmt={(int)format} normalizedFmt={(int)placeholderFormat}"
                );
                return false;
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        _placeholderTextureWidth = width;
        _placeholderTextureHeight = height;
        _placeholderTextureFormat = placeholderFormat;
        return true;
    }

    private bool TryBuildPlaceholderQuadLayer(out CompositionLayerQuad quadLayer)
    {
        quadLayer = default;
        if (_xr is null || _localSpace.Handle == 0 || _placeholderQuadSwapchain.Handle == 0)
        {
            return false;
        }

        var acquireInfo = new SwapchainImageAcquireInfo
        {
            Type = StructureType.SwapchainImageAcquireInfo,
        };
        uint imageIndex = 0;
        var acquireResult = _xr.AcquireSwapchainImage(
            _placeholderQuadSwapchain,
            ref acquireInfo,
            ref imageIndex
        );
        if (acquireResult != Result.Success)
        {
            return false;
        }

        try
        {
            var waitInfo = new SwapchainImageWaitInfo
            {
                Type = StructureType.SwapchainImageWaitInfo,
                Timeout = long.MaxValue,
            };
            var waitResult = _xr.WaitSwapchainImage(_placeholderQuadSwapchain, ref waitInfo);
            if (waitResult != Result.Success)
            {
                return false;
            }

            var targetTexture = (ID3D11Texture2D*)
                _placeholderQuadSwapchainImages[imageIndex].Texture;
            if (!ShowPlaceholderTexture(targetTexture))
            {
                return false;
            }

            quadLayer = new CompositionLayerQuad
            {
                Type = StructureType.CompositionLayerQuad,
                Space = _localSpace,
                EyeVisibility = EyeVisibility.Both,
                Pose = new Posef
                {
                    Orientation = new Quaternionf
                    {
                        X = 0,
                        Y = 0,
                        Z = 0,
                        W = 1,
                    },
                    Position = new Vector3f
                    {
                        X = 0,
                        Y = 0,
                        Z = -1.0f,
                    },
                },
                Size = new Extent2Df { Width = 1.2f, Height = 0.675f },
                SubImage = new SwapchainSubImage
                {
                    Swapchain = _placeholderQuadSwapchain,
                    ImageRect = new Rect2Di
                    {
                        Offset = new Offset2Di { X = 0, Y = 0 },
                        Extent = new Extent2Di
                        {
                            Width = (int)_placeholderQuadSwapchainWidth,
                            Height = (int)_placeholderQuadSwapchainHeight,
                        },
                    },
                    ImageArrayIndex = 0,
                },
            };
            return true;
        }
        finally
        {
            var releaseInfo = new SwapchainImageReleaseInfo
            {
                Type = StructureType.SwapchainImageReleaseInfo,
            };
            _ = _xr.ReleaseSwapchainImage(_placeholderQuadSwapchain, ref releaseInfo);
        }
    }

    private bool TryConvertSourceTextureToSwapchainFormat(
        ID3D11Texture2D* sourceTexture,
        uint sourceSubresourceIndex,
        Texture2DDesc sourceTextureDesc,
        uint targetHeight,
        Format targetFormat,
        out ID3D11Texture2D* convertedTexture,
        out int failureCode
    )
    {
        convertedTexture = null;
        failureCode = 31;
        if (
            !EnsureVideoProcessorForFormatConversion(
                sourceTexture,
                sourceSubresourceIndex,
                sourceTextureDesc,
                targetHeight,
                targetFormat,
                out failureCode
            )
        )
        {
            return false;
        }

        if (
            _videoProcessor is null
            || _videoProcessorOutputView is null
            || _videoProcessorInputView is null
            || _videoProcessorOutputTexture is null
            || _d3d11VideoContext is null
        )
        {
            failureCode = 34;
            return false;
        }

        _d3d11VideoContext->VideoProcessorSetStreamFrameFormat(
            _videoProcessor,
            0,
            VideoFrameFormat.Progressive
        );
        _d3d11VideoContext->VideoProcessorSetStreamAutoProcessingMode(_videoProcessor, 0, 0);

        var stream = new VideoProcessorStream
        {
            Enable = 1,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            PpPastSurfaces = null,
            PInputSurface = _videoProcessorInputView,
            PpFutureSurfaces = null,
            PpPastSurfacesRight = null,
            PInputSurfaceRight = null,
            PpFutureSurfacesRight = null,
        };

        var hr = _d3d11VideoContext->VideoProcessorBlt(
            _videoProcessor,
            _videoProcessorOutputView,
            0,
            1,
            &stream
        );
        if (hr < 0)
        {
            failureCode = 42;
            return false;
        }

        convertedTexture = _videoProcessorOutputTexture;
        failureCode = 0;
        return true;
    }

    private bool EnsureVideoProcessorForFormatConversion(
        ID3D11Texture2D* sourceTexture,
        uint sourceSubresourceIndex,
        Texture2DDesc sourceTextureDesc,
        uint targetHeight,
        Format targetFormat,
        out int failureCode
    )
    {
        failureCode = 0;
        if (_d3d11Device is null || _d3d11DeviceContext is null)
        {
            failureCode = 35;
            return false;
        }

        if (_d3d11VideoDevice is null)
        {
            void* videoDevicePointer = null;
            var videoDeviceGuid = Id3D11VideoDeviceGuid;
            if (
                _d3d11Device->QueryInterface(ref videoDeviceGuid, ref videoDevicePointer) < 0
                || videoDevicePointer is null
            )
            {
                failureCode = 36;
                return false;
            }

            _d3d11VideoDevice = (ID3D11VideoDevice*)videoDevicePointer;
        }

        if (_d3d11VideoContext is null)
        {
            void* videoContextPointer = null;
            var videoContextGuid = Id3D11VideoContextGuid;
            if (
                _d3d11DeviceContext->QueryInterface(ref videoContextGuid, ref videoContextPointer)
                    < 0
                || videoContextPointer is null
            )
            {
                failureCode = 37;
                return false;
            }

            _d3d11VideoContext = (ID3D11VideoContext*)videoContextPointer;
        }

        var needsRecreateProcessor =
            _videoProcessorEnumerator is null
            || _videoProcessor is null
            || _videoProcessorOutputTexture is null
            || _videoProcessorOutputView is null
            || _videoProcessorInputWidth != sourceTextureDesc.Width
            || _videoProcessorInputHeight != sourceTextureDesc.Height
            || _videoProcessorOutputWidth != sourceTextureDesc.Width
            || _videoProcessorOutputHeight != targetHeight
            || _videoProcessorOutputFormat != targetFormat;
        if (needsRecreateProcessor)
        {
            var videoProcessorTargetFormat = NormalizeVideoProcessorTargetFormat(targetFormat);
            var usageCandidates = new[]
            {
                _videoProcessorUsage,
                VideoUsage.OptimalSpeed,
                (VideoUsage)0,
                (VideoUsage)2,
            };
            var recreateSucceeded = false;
            var recreateFailureCode = 43;
            foreach (var usage in usageCandidates.Distinct())
            {
                ReleaseVideoProcessorWorkingSet();

                var contentDesc = new VideoProcessorContentDesc
                {
                    InputFrameFormat = VideoFrameFormat.Progressive,
                    InputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                    InputWidth = sourceTextureDesc.Width,
                    InputHeight = sourceTextureDesc.Height,
                    OutputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                    OutputWidth = sourceTextureDesc.Width,
                    OutputHeight = targetHeight,
                    Usage = usage,
                };

                var createEnumeratorHr = _d3d11VideoDevice->CreateVideoProcessorEnumerator(
                    ref contentDesc,
                    ref _videoProcessorEnumerator
                );
                if (createEnumeratorHr < 0)
                {
                    recreateFailureCode = 38;
                    LogVideoProcessorFailureDetail(
                        $"code=38 hr=0x{createEnumeratorHr:X8} usage={(int)usage} in={sourceTextureDesc.Width}x{sourceTextureDesc.Height} out={sourceTextureDesc.Width}x{targetHeight}"
                    );
                    continue;
                }

                uint sourceSupport = 0;
                var sourceFormatCheckHr = _videoProcessorEnumerator->CheckVideoProcessorFormat(
                    sourceTextureDesc.Format,
                    ref sourceSupport
                );
                if (sourceFormatCheckHr < 0)
                {
                    recreateFailureCode = 39;
                    LogVideoProcessorFailureDetail(
                        $"code=39 hr=0x{sourceFormatCheckHr:X8} usage={(int)usage} srcFmt={(int)sourceTextureDesc.Format}"
                    );
                    continue;
                }

                var inputSupported =
                    ((VideoProcessorFormatSupport)sourceSupport & VideoProcessorFormatSupport.Input)
                    != 0;
                if (!inputSupported)
                {
                    recreateFailureCode = 40;
                    LogVideoProcessorFailureDetail(
                        $"code=40 usage={(int)usage} srcFmt={(int)sourceTextureDesc.Format} support=0x{sourceSupport:X8}"
                    );
                    continue;
                }

                uint outputSupport = 0;
                var outputFormatCheckHr = _videoProcessorEnumerator->CheckVideoProcessorFormat(
                    videoProcessorTargetFormat,
                    ref outputSupport
                );
                if (outputFormatCheckHr < 0)
                {
                    recreateFailureCode = 41;
                    LogVideoProcessorFailureDetail(
                        $"code=41 hr=0x{outputFormatCheckHr:X8} usage={(int)usage} outFmt={(int)videoProcessorTargetFormat} targetFmtRaw={(int)targetFormat}"
                    );
                    continue;
                }

                var outputSupported =
                    (
                        (VideoProcessorFormatSupport)outputSupport
                        & VideoProcessorFormatSupport.Output
                    ) != 0;
                if (!outputSupported)
                {
                    recreateFailureCode = 43;
                    LogVideoProcessorFailureDetail(
                        $"code=43 usage={(int)usage} srcFmt={(int)sourceTextureDesc.Format} outFmt={(int)videoProcessorTargetFormat} targetFmtRaw={(int)targetFormat} support=0x{outputSupport:X8} in={sourceTextureDesc.Width}x{sourceTextureDesc.Height} out={sourceTextureDesc.Width}x{targetHeight}"
                    );
                    continue;
                }

                var createProcessorHr = _d3d11VideoDevice->CreateVideoProcessor(
                    _videoProcessorEnumerator,
                    0,
                    ref _videoProcessor
                );
                if (createProcessorHr < 0)
                {
                    recreateFailureCode = 44;
                    LogVideoProcessorFailureDetail(
                        $"code=44 hr=0x{createProcessorHr:X8} usage={(int)usage}"
                    );
                    continue;
                }

                var outputDesc = new Texture2DDesc
                {
                    Width = sourceTextureDesc.Width,
                    Height = targetHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = videoProcessorTargetFormat,
                    SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                    Usage = Usage.Default,
                    BindFlags = 0,
                    CPUAccessFlags = 0,
                    MiscFlags = 0,
                };
                var bindFlagCandidates = new[]
                {
                    (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource | BindFlag.VideoEncoder),
                    (uint)(BindFlag.RenderTarget | BindFlag.VideoEncoder),
                    (uint)BindFlag.RenderTarget,
                };
                var outputViewCreated = false;
                foreach (var bindFlags in bindFlagCandidates.Distinct())
                {
                    outputDesc.BindFlags = bindFlags;
                    if (
                        _d3d11Device->CreateTexture2D(
                            ref outputDesc,
                            (SubresourceData*)0,
                            ref _videoProcessorOutputTexture
                        ) < 0
                    )
                    {
                        recreateFailureCode = 45;
                        LogVideoProcessorFailureDetail(
                            $"code=45 usage={(int)usage} bind=0x{bindFlags:X8} texFmt={(int)videoProcessorTargetFormat} size={sourceTextureDesc.Width}x{targetHeight}"
                        );
                        continue;
                    }

                    var outputViewDesc = new VideoProcessorOutputViewDesc
                    {
                        ViewDimension = VpovDimension.Texture2D,
                        Anonymous = new VideoProcessorOutputViewDescUnion
                        {
                            Texture2D = new Tex2DVpov { MipSlice = 0 },
                        },
                    };
                    var outputViewResult = _d3d11VideoDevice->CreateVideoProcessorOutputView(
                        (ID3D11Resource*)_videoProcessorOutputTexture,
                        _videoProcessorEnumerator,
                        ref outputViewDesc,
                        ref _videoProcessorOutputView
                    );
                    if (outputViewResult >= 0)
                    {
                        outputViewCreated = true;
                        break;
                    }

                    recreateFailureCode = 46;
                    LogVideoProcessorFailureDetail(
                        $"code=46 hr=0x{outputViewResult:X8} usage={(int)usage} bind=0x{bindFlags:X8} texFmt={(int)videoProcessorTargetFormat}"
                    );
                    _ = _videoProcessorOutputTexture->Release();
                    _videoProcessorOutputTexture = null;
                }

                if (!outputViewCreated)
                {
                    continue;
                }

                _videoProcessorUsage = usage;
                _videoProcessorInputWidth = sourceTextureDesc.Width;
                _videoProcessorInputHeight = sourceTextureDesc.Height;
                _videoProcessorOutputWidth = sourceTextureDesc.Width;
                _videoProcessorOutputHeight = targetHeight;
                _videoProcessorOutputFormat = targetFormat;
                _videoProcessorInputTexturePointer = IntPtr.Zero;
                _videoProcessorInputSubresourceIndex = uint.MaxValue;
                recreateSucceeded = true;
                lock (_videoFrameLock)
                {
                    _lastVideoProcessorFailureDetail = string.Empty;
                }
                break;
            }

            if (!recreateSucceeded)
            {
                failureCode = recreateFailureCode;
                return false;
            }
        }

        if (
            _videoProcessorInputView is null
            || _videoProcessorInputTexturePointer != (nint)sourceTexture
            || _videoProcessorInputSubresourceIndex != sourceSubresourceIndex
        )
        {
            ReleaseVideoProcessorInputView();

            var mipLevels = sourceTextureDesc.MipLevels == 0 ? 1u : sourceTextureDesc.MipLevels;
            var mipSlice = sourceSubresourceIndex % mipLevels;
            var arraySlice = sourceSubresourceIndex / mipLevels;
            var inputViewDesc = new VideoProcessorInputViewDesc
            {
                FourCC = 0,
                ViewDimension = VpivDimension.Texture2D,
                Anonymous = new VideoProcessorInputViewDescUnion
                {
                    Texture2D = new Tex2DVpiv { MipSlice = mipSlice, ArraySlice = arraySlice },
                },
            };
            if (
                _d3d11VideoDevice->CreateVideoProcessorInputView(
                    (ID3D11Resource*)sourceTexture,
                    _videoProcessorEnumerator,
                    ref inputViewDesc,
                    ref _videoProcessorInputView
                ) < 0
            )
            {
                failureCode = 47;
                return false;
            }

            _videoProcessorInputTexturePointer = (nint)sourceTexture;
            _videoProcessorInputSubresourceIndex = sourceSubresourceIndex;
        }

        return true;
    }

    private bool EnsureVideoProcessorForTargetBlit(
        ID3D11Texture2D* sourceTexture,
        uint sourceSubresourceIndex,
        Texture2DDesc sourceTextureDesc,
        uint targetWidth,
        uint targetHeight,
        Format targetFormat,
        out int failureCode
    )
    {
        failureCode = 0;
        if (_d3d11Device is null || _d3d11DeviceContext is null)
        {
            failureCode = 53;
            return false;
        }

        if (_d3d11VideoDevice is null)
        {
            void* videoDevicePointer = null;
            var videoDeviceGuid = Id3D11VideoDeviceGuid;
            if (
                _d3d11Device->QueryInterface(ref videoDeviceGuid, ref videoDevicePointer) < 0
                || videoDevicePointer is null
            )
            {
                failureCode = 54;
                return false;
            }

            _d3d11VideoDevice = (ID3D11VideoDevice*)videoDevicePointer;
        }

        if (_d3d11VideoContext is null)
        {
            void* videoContextPointer = null;
            var videoContextGuid = Id3D11VideoContextGuid;
            if (
                _d3d11DeviceContext->QueryInterface(ref videoContextGuid, ref videoContextPointer)
                    < 0
                || videoContextPointer is null
            )
            {
                failureCode = 55;
                return false;
            }

            _d3d11VideoContext = (ID3D11VideoContext*)videoContextPointer;
        }

        var needsRecreateProcessor =
            _videoProcessorEnumerator is null
            || _videoProcessor is null
            || _videoProcessorOutputTexture is null
            || _videoProcessorOutputView is null
            || _videoProcessorInputWidth != sourceTextureDesc.Width
            || _videoProcessorInputHeight != sourceTextureDesc.Height
            || _videoProcessorOutputWidth != targetWidth
            || _videoProcessorOutputHeight != targetHeight
            || _videoProcessorOutputFormat != targetFormat;
        if (needsRecreateProcessor)
        {
            var videoProcessorTargetFormat = NormalizeVideoProcessorTargetFormat(targetFormat);
            var usageCandidates = new[]
            {
                _videoProcessorUsage,
                VideoUsage.OptimalSpeed,
                (VideoUsage)0,
                (VideoUsage)2,
            };
            var recreateSucceeded = false;
            var recreateFailureCode = 56;
            foreach (var usage in usageCandidates.Distinct())
            {
                ReleaseVideoProcessorWorkingSet();

                var contentDesc = new VideoProcessorContentDesc
                {
                    InputFrameFormat = VideoFrameFormat.Progressive,
                    InputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                    InputWidth = sourceTextureDesc.Width,
                    InputHeight = sourceTextureDesc.Height,
                    OutputFrameRate = new Rational { Numerator = 60, Denominator = 1 },
                    OutputWidth = targetWidth,
                    OutputHeight = targetHeight,
                    Usage = usage,
                };

                var createEnumeratorHr = _d3d11VideoDevice->CreateVideoProcessorEnumerator(
                    ref contentDesc,
                    ref _videoProcessorEnumerator
                );
                if (createEnumeratorHr < 0)
                {
                    recreateFailureCode = 57;
                    LogVideoProcessorFailureDetail(
                        $"code=57 hr=0x{createEnumeratorHr:X8} usage={(int)usage} in={sourceTextureDesc.Width}x{sourceTextureDesc.Height} out={targetWidth}x{targetHeight}"
                    );
                    continue;
                }

                uint sourceSupport = 0;
                var sourceFormatCheckHr = _videoProcessorEnumerator->CheckVideoProcessorFormat(
                    sourceTextureDesc.Format,
                    ref sourceSupport
                );
                if (sourceFormatCheckHr < 0)
                {
                    recreateFailureCode = 58;
                    LogVideoProcessorFailureDetail(
                        $"code=58 hr=0x{sourceFormatCheckHr:X8} usage={(int)usage} srcFmt={(int)sourceTextureDesc.Format}"
                    );
                    continue;
                }

                var inputSupported =
                    ((VideoProcessorFormatSupport)sourceSupport & VideoProcessorFormatSupport.Input)
                    != 0;
                if (!inputSupported)
                {
                    recreateFailureCode = 59;
                    LogVideoProcessorFailureDetail(
                        $"code=59 usage={(int)usage} srcFmt={(int)sourceTextureDesc.Format} support=0x{sourceSupport:X8}"
                    );
                    continue;
                }

                uint outputSupport = 0;
                var outputFormatCheckHr = _videoProcessorEnumerator->CheckVideoProcessorFormat(
                    videoProcessorTargetFormat,
                    ref outputSupport
                );
                if (outputFormatCheckHr < 0)
                {
                    recreateFailureCode = 60;
                    LogVideoProcessorFailureDetail(
                        $"code=60 hr=0x{outputFormatCheckHr:X8} usage={(int)usage} outFmt={(int)videoProcessorTargetFormat} targetFmtRaw={(int)targetFormat}"
                    );
                    continue;
                }

                var outputSupported =
                    (
                        (VideoProcessorFormatSupport)outputSupport
                        & VideoProcessorFormatSupport.Output
                    ) != 0;
                if (!outputSupported)
                {
                    recreateFailureCode = 61;
                    LogVideoProcessorFailureDetail(
                        $"code=61 usage={(int)usage} srcFmt={(int)sourceTextureDesc.Format} outFmt={(int)videoProcessorTargetFormat} targetFmtRaw={(int)targetFormat} support=0x{outputSupport:X8} in={sourceTextureDesc.Width}x{sourceTextureDesc.Height} out={targetWidth}x{targetHeight}"
                    );
                    continue;
                }

                var createProcessorHr = _d3d11VideoDevice->CreateVideoProcessor(
                    _videoProcessorEnumerator,
                    0,
                    ref _videoProcessor
                );
                if (createProcessorHr < 0)
                {
                    recreateFailureCode = 62;
                    LogVideoProcessorFailureDetail(
                        $"code=62 hr=0x{createProcessorHr:X8} usage={(int)usage}"
                    );
                    continue;
                }

                var outputDesc = new Texture2DDesc
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = videoProcessorTargetFormat,
                    SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                    Usage = Usage.Default,
                    BindFlags = 0,
                    CPUAccessFlags = 0,
                    MiscFlags = 0,
                };
                var bindFlagCandidates = new[]
                {
                    (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource | BindFlag.VideoEncoder),
                    (uint)(BindFlag.RenderTarget | BindFlag.VideoEncoder),
                    (uint)BindFlag.RenderTarget,
                };
                var outputViewCreated = false;
                foreach (var bindFlags in bindFlagCandidates.Distinct())
                {
                    outputDesc.BindFlags = bindFlags;
                    if (
                        _d3d11Device->CreateTexture2D(
                            ref outputDesc,
                            (SubresourceData*)0,
                            ref _videoProcessorOutputTexture
                        ) < 0
                    )
                    {
                        recreateFailureCode = 64;
                        LogVideoProcessorFailureDetail(
                            $"code=64 usage={(int)usage} bind=0x{bindFlags:X8} texFmt={(int)videoProcessorTargetFormat} size={targetWidth}x{targetHeight}"
                        );
                        continue;
                    }

                    var outputViewDesc = new VideoProcessorOutputViewDesc
                    {
                        ViewDimension = VpovDimension.Texture2D,
                        Anonymous = new VideoProcessorOutputViewDescUnion
                        {
                            Texture2D = new Tex2DVpov { MipSlice = 0 },
                        },
                    };
                    var outputViewResult = _d3d11VideoDevice->CreateVideoProcessorOutputView(
                        (ID3D11Resource*)_videoProcessorOutputTexture,
                        _videoProcessorEnumerator,
                        ref outputViewDesc,
                        ref _videoProcessorOutputView
                    );
                    if (outputViewResult >= 0)
                    {
                        outputViewCreated = true;
                        break;
                    }

                    recreateFailureCode = 65;
                    LogVideoProcessorFailureDetail(
                        $"code=65 hr=0x{outputViewResult:X8} usage={(int)usage} bind=0x{bindFlags:X8} texFmt={(int)videoProcessorTargetFormat}"
                    );
                    _ = _videoProcessorOutputTexture->Release();
                    _videoProcessorOutputTexture = null;
                }

                if (!outputViewCreated)
                {
                    continue;
                }

                _videoProcessorUsage = usage;
                _videoProcessorInputWidth = sourceTextureDesc.Width;
                _videoProcessorInputHeight = sourceTextureDesc.Height;
                _videoProcessorOutputWidth = targetWidth;
                _videoProcessorOutputHeight = targetHeight;
                _videoProcessorOutputFormat = targetFormat;
                _videoProcessorInputTexturePointer = IntPtr.Zero;
                _videoProcessorInputSubresourceIndex = uint.MaxValue;
                recreateSucceeded = true;
                lock (_videoFrameLock)
                {
                    _lastVideoProcessorFailureDetail = string.Empty;
                }
                break;
            }

            if (!recreateSucceeded)
            {
                failureCode = recreateFailureCode;
                return false;
            }
        }

        if (
            _videoProcessorInputView is null
            || _videoProcessorInputTexturePointer != (nint)sourceTexture
            || _videoProcessorInputSubresourceIndex != sourceSubresourceIndex
        )
        {
            ReleaseVideoProcessorInputView();

            var mipLevels = sourceTextureDesc.MipLevels == 0 ? 1u : sourceTextureDesc.MipLevels;
            var mipSlice = sourceSubresourceIndex % mipLevels;
            var arraySlice = sourceSubresourceIndex / mipLevels;
            var inputViewDesc = new VideoProcessorInputViewDesc
            {
                FourCC = 0,
                ViewDimension = VpivDimension.Texture2D,
                Anonymous = new VideoProcessorInputViewDescUnion
                {
                    Texture2D = new Tex2DVpiv { MipSlice = mipSlice, ArraySlice = arraySlice },
                },
            };
            if (
                _d3d11VideoDevice->CreateVideoProcessorInputView(
                    (ID3D11Resource*)sourceTexture,
                    _videoProcessorEnumerator,
                    ref inputViewDesc,
                    ref _videoProcessorInputView
                ) < 0
            )
            {
                failureCode = 63;
                return false;
            }

            _videoProcessorInputTexturePointer = (nint)sourceTexture;
            _videoProcessorInputSubresourceIndex = sourceSubresourceIndex;
        }

        return true;
    }

    private static Format NormalizeVideoProcessorTargetFormat(Format format)
    {
        if ((int)format == 27)
        {
            return Format.FormatR8G8B8A8Unorm;
        }

        if ((int)format == 29)
        {
            return Format.FormatR8G8B8A8Unorm;
        }

        if ((int)format == 90)
        {
            return Format.FormatB8G8R8A8Unorm;
        }

        if ((int)format == 91)
        {
            return Format.FormatB8G8R8A8Unorm;
        }

        return format;
    }

    private static bool CanCopyWithoutFormatConversion(Format sourceFormat, Format targetFormat)
    {
        if (sourceFormat == targetFormat)
        {
            return true;
        }

        return AreCompatibleCopyFormats(sourceFormat, targetFormat);
    }

    private static bool AreCompatibleCopyFormats(Format left, Format right)
    {
        return (IsRgbaCopyCompatibleFormat(left) && IsRgbaCopyCompatibleFormat(right))
            || (IsBgraCopyCompatibleFormat(left) && IsBgraCopyCompatibleFormat(right));
    }

    private static bool IsRgbaCopyCompatibleFormat(Format format)
    {
        return format == Format.FormatR8G8B8A8Unorm || (int)format == 29;
    }

    private static bool IsBgraCopyCompatibleFormat(Format format)
    {
        return format == Format.FormatB8G8R8A8Unorm || (int)format == 91;
    }

    private void LogVideoProcessorFailureDetail(string detail)
    {
        if (_logger is null)
        {
            return;
        }

        lock (_videoFrameLock)
        {
            if (string.Equals(_lastVideoProcessorFailureDetail, detail, StringComparison.Ordinal))
            {
                return;
            }

            _lastVideoProcessorFailureDetail = detail;
        }

        _logger.Debug($"VideoProcessor setup failed: {detail}");
    }

    private void ReleaseVideoProcessorInputView()
    {
        if (_videoProcessorInputView is not null)
        {
            _ = _videoProcessorInputView->Release();
            _videoProcessorInputView = null;
        }

        _videoProcessorInputTexturePointer = IntPtr.Zero;
        _videoProcessorInputSubresourceIndex = uint.MaxValue;
    }

    private void ReleaseVideoProcessorWorkingSet()
    {
        ReleaseVideoProcessorInputView();

        if (_videoProcessorOutputView is not null)
        {
            _ = _videoProcessorOutputView->Release();
            _videoProcessorOutputView = null;
        }

        if (_videoProcessorOutputTexture is not null)
        {
            _ = _videoProcessorOutputTexture->Release();
            _videoProcessorOutputTexture = null;
        }

        if (_videoProcessor is not null)
        {
            _ = _videoProcessor->Release();
            _videoProcessor = null;
        }

        if (_videoProcessorEnumerator is not null)
        {
            _ = _videoProcessorEnumerator->Release();
            _videoProcessorEnumerator = null;
        }

        _videoProcessorInputWidth = 0;
        _videoProcessorInputHeight = 0;
        _videoProcessorOutputWidth = 0;
        _videoProcessorOutputHeight = 0;
        _videoProcessorOutputFormat = 0;
    }

    private void ReleaseVideoProcessorResources()
    {
        ReleaseVideoProcessorWorkingSet();

        if (_placeholderTexture is not null)
        {
            _ = _placeholderTexture->Release();
            _placeholderTexture = null;
        }
        _placeholderTextureWidth = 0;
        _placeholderTextureHeight = 0;
        _placeholderTextureFormat = 0;

        if (_blackClearTexture is not null)
        {
            _ = _blackClearTexture->Release();
            _blackClearTexture = null;
        }
        _blackClearTextureWidth = 0;
        _blackClearTextureHeight = 0;
        _blackClearTextureFormat = 0;

        if (_d3d11VideoContext is not null)
        {
            _ = _d3d11VideoContext->Release();
            _d3d11VideoContext = null;
        }

        if (_d3d11VideoDevice is not null)
        {
            _ = _d3d11VideoDevice->Release();
            _d3d11VideoDevice = null;
        }
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
