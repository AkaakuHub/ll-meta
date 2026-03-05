using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Utils;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace LLMeta.App;

public partial class App
{
    private const int DecodeCatchupQueueDepth = 3;
    private const int DecodeCatchupQueueDelayMs = 60;
    private const int ReceiveStallThresholdMs = 1500;
    private const int DecodeStallThresholdMs = 1200;
    private const int KeyFrameRequestIntervalMs = 1200;
    private const int Vp8KeyFrameRequestIntervalMs = 250;
    private const int Vp8NoFrameKeyFrameThreshold = 8;
    private const int VideoLoopStallLogThresholdMs = 2000;
    private const int VideoLoopStallLogIntervalMs = 1500;
    private const int RenderStallLogThresholdMs = 1500;
    private const int RenderStallLogIntervalMs = 1500;

    private async Task VideoDecodeLoopAsync(CancellationToken token, AppLogger logger)
    {
        UpdateVideoLoopCheckpoint("loop-start");
        while (!token.IsCancellationRequested)
        {
            try
            {
                UpdateVideoLoopCheckpoint("loop-iteration");
                var activeWebRtcPeerConnectionService = _webRtcPeerConnectionService;
                var activeVideoDecodeService = _videoH264DecodeService;
                if (
                    activeVideoDecodeService is not null
                    && _openXrControllerInputService is not null
                )
                {
                    UpdateVideoLoopCheckpoint("set-d3d11-device");
                    activeVideoDecodeService.SetD3D11DevicePointer(
                        _openXrControllerInputService.GetD3D11DevicePointer()
                    );
                    UpdateVideoLoopCheckpoint("set-d3d11-device-done");
                }
                var queueSnapshot = activeWebRtcPeerConnectionService?.GetVideoStatsSnapshot();
                var currentVideoCodecName =
                    activeWebRtcPeerConnectionService?.GetCurrentVideoCodecName() ?? "unknown";
                var isVp8Stream = currentVideoCodecName.Equals(
                    "VP8",
                    StringComparison.OrdinalIgnoreCase
                );
                var keyFrameRequestIntervalMs = isVp8Stream
                    ? Vp8KeyFrameRequestIntervalMs
                    : KeyFrameRequestIntervalMs;
                bool waitingForKeyFrame;
                uint consecutiveNoFrameDecodes;
                lock (_runtimeStateLock)
                {
                    waitingForKeyFrame = _isWaitingForVideoKeyFrame;
                    consecutiveNoFrameDecodes = _videoConsecutiveNoFrameDecodes;
                }
                var shouldCatchupToLatest =
                    queueSnapshot is not null
                    && !isVp8Stream
                    && !waitingForKeyFrame
                    && consecutiveNoFrameDecodes < 3
                    && (
                        queueSnapshot.Value.QueueDepth >= DecodeCatchupQueueDepth
                        || queueSnapshot.Value.LastLatencyMs >= DecodeCatchupQueueDelayMs
                    );
                VideoFramePacket encodedPacket = default;
                var hasPacket =
                    activeWebRtcPeerConnectionService is not null
                    && (
                        shouldCatchupToLatest
                            ? activeWebRtcPeerConnectionService.TryDequeueLatestVideoFrame(
                                out encodedPacket
                            )
                            : activeWebRtcPeerConnectionService.TryDequeueVideoFrame(
                                out encodedPacket
                            )
                    );
                if (
                    activeWebRtcPeerConnectionService is null
                    || activeVideoDecodeService is null
                    || !hasPacket
                )
                {
                    UpdateVideoLoopCheckpoint("wait-packet");
                    if (activeWebRtcPeerConnectionService is not null && queueSnapshot is not null)
                    {
                        var stallNow = DateTimeOffset.UtcNow;
                        var nowUnixMs = stallNow.ToUnixTimeMilliseconds();
                        var lastRxUnixMs = Math.Max(
                            (long)queueSnapshot.Value.LastTimestampUnixMs,
                            (long)queueSnapshot.Value.LastRtpTimestampUnixMs
                        );
                        var receiveStalled =
                            queueSnapshot.Value.IsConnected
                            && queueSnapshot.Value.QueueDepth == 0
                            && lastRxUnixMs > 0
                            && nowUnixMs - lastRxUnixMs >= ReceiveStallThresholdMs;
                        if (receiveStalled)
                        {
                            lock (_runtimeStateLock)
                            {
                                if (
                                    _lastVideoKeyFrameRequestAt == DateTimeOffset.MinValue
                                    || (stallNow - _lastVideoKeyFrameRequestAt).TotalMilliseconds
                                        >= keyFrameRequestIntervalMs
                                )
                                {
                                    _lastVideoKeyFrameRequestAt = stallNow;
                                    _isWaitingForVideoKeyFrame = true;
                                    _lastVideoDecodeStatus = "rx stalled; keyframe requested";
                                    activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                                }
                                else
                                {
                                    _lastVideoDecodeStatus = "rx stalled; waiting keyframe";
                                }
                            }
                        }
                    }

                    MaybeLogVideoPipelineStats(logger);
                    await Task.Yield();
                    continue;
                }

                lock (_runtimeStateLock)
                {
                    _videoFramesObserved += 1;
                }

                if (encodedPacket.ConnectionId != _videoConnectionId)
                {
                    _videoH264DecodeService?.Dispose();
                    _videoH264DecodeService = new VideoH264DecodeService(logger);
                    if (_openXrControllerInputService is not null)
                    {
                        _videoH264DecodeService.SetD3D11DevicePointer(
                            _openXrControllerInputService.GetD3D11DevicePointer()
                        );
                    }
                    activeVideoDecodeService = _videoH264DecodeService;
                    lock (_runtimeStateLock)
                    {
                        _videoConnectionId = encodedPacket.ConnectionId;
                        _lastVideoKeyFrameRequestAt = DateTimeOffset.MinValue;
                        _videoFramesObserved = 1;
                        _videoDecodeCalls = 0;
                        _videoDecodedFrames = 0;
                        _lastVideoDecodeStatus = "none";
                        _videoConsecutiveNoFrameDecodes = 0;
                        _lastVideoDecodedAt = DateTimeOffset.MinValue;
                        _isWaitingForVideoKeyFrame = true;
                    }
                    activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                    logger.Info(
                        "Video pipeline new connection: "
                            + $"conn={_videoConnectionId} seq={encodedPacket.Sequence} payload={encodedPacket.Payload.Length}"
                    );
                }

                var now = DateTimeOffset.UtcNow;
                bool shouldDecodeCurrentPacket;
                lock (_runtimeStateLock)
                {
                    shouldDecodeCurrentPacket =
                        !_isWaitingForVideoKeyFrame || encodedPacket.IsKeyFrame;
                    if (_isWaitingForVideoKeyFrame && encodedPacket.IsKeyFrame)
                    {
                        _isWaitingForVideoKeyFrame = false;
                        logger.Info("Video pipeline sync: keyframe received, decode resumed.");
                    }
                }

                if (!shouldDecodeCurrentPacket)
                {
                    lock (_runtimeStateLock)
                    {
                        if (
                            _lastVideoKeyFrameRequestAt == DateTimeOffset.MinValue
                            || (now - _lastVideoKeyFrameRequestAt).TotalMilliseconds
                                >= keyFrameRequestIntervalMs
                        )
                        {
                            _lastVideoKeyFrameRequestAt = now;
                            activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                        }
                        _lastVideoDecodeStatus = "waiting keyframe";
                    }
                    MaybeLogVideoPipelineStats(logger);
                    await Task.Yield();
                    continue;
                }

                lock (_runtimeStateLock)
                {
                    _videoDecodeCalls += 1;
                }
                var decodeStartedAt = DateTimeOffset.UtcNow;
                var decodeStopwatch = Stopwatch.StartNew();
                UpdateVideoLoopCheckpoint(
                    $"decode-start conn={encodedPacket.ConnectionId} seq={encodedPacket.Sequence}"
                );
                var decodeStatus = activeVideoDecodeService.Decode(encodedPacket);
                UpdateVideoLoopCheckpoint(
                    $"decode-done conn={encodedPacket.ConnectionId} seq={encodedPacket.Sequence} status={decodeStatus}"
                );
                decodeStopwatch.Stop();
                var decodeCompletedAt = DateTimeOffset.UtcNow;
                lock (_runtimeStateLock)
                {
                    _lastVideoDecodeStatus = decodeStatus;
                    _lastDecodeElapsedMs = decodeStopwatch.ElapsedMilliseconds;
                    if (decodeStatus == "decoded frame")
                    {
                        _videoConsecutiveNoFrameDecodes = 0;
                        _lastVideoDecodedAt = now;
                    }
                    else
                    {
                        _videoConsecutiveNoFrameDecodes += 1;
                    }
                }

                if (decodeStopwatch.ElapsedMilliseconds >= DecodeStallThresholdMs)
                {
                    logger.Info(
                        "Video decode stall detected; decoder will be recreated. "
                            + $"elapsedMs={decodeStopwatch.ElapsedMilliseconds} seq={encodedPacket.Sequence}"
                    );
                    _videoH264DecodeService?.Dispose();
                    _videoH264DecodeService = new VideoH264DecodeService(logger);
                    if (_openXrControllerInputService is not null)
                    {
                        _videoH264DecodeService.SetD3D11DevicePointer(
                            _openXrControllerInputService.GetD3D11DevicePointer()
                        );
                    }

                    lock (_runtimeStateLock)
                    {
                        _isWaitingForVideoKeyFrame = true;
                        _videoConsecutiveNoFrameDecodes = 0;
                        _lastVideoKeyFrameRequestAt = now;
                        _lastVideoDecodeStatus = "decode stall; keyframe requested";
                    }

                    activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                    MaybeLogVideoPipelineStats(logger);
                    await Task.Yield();
                    continue;
                }

                if (
                    _videoH264DecodeService is not null
                    && _videoH264DecodeService.TryGetLatestFrame(out var decodedFrame)
                    && _openXrControllerInputService is not null
                )
                {
                    UpdateVideoLoopCheckpoint($"upload-start seq={decodedFrame.Sequence}");
                    lock (_runtimeStateLock)
                    {
                        _videoDecodedFrames += 1;
                        _lastVideoDecodedAt = now;
                    }
                    _openXrControllerInputService.SetLatestDecodedSbsFrame(decodedFrame);
                    UpdateVideoLoopCheckpoint($"upload-done seq={decodedFrame.Sequence}");
                }

                lock (_runtimeStateLock)
                {
                    var stalledForMs =
                        _lastVideoDecodedAt == DateTimeOffset.MinValue
                            ? double.MaxValue
                            : (now - _lastVideoDecodedAt).TotalMilliseconds;
                    var noFrameKeyFrameThreshold = isVp8Stream ? Vp8NoFrameKeyFrameThreshold : 45;
                    var shouldRequestKeyFrame =
                        _videoConsecutiveNoFrameDecodes >= noFrameKeyFrameThreshold
                        && stalledForMs >= (isVp8Stream ? 220 : 1200);
                    if (shouldRequestKeyFrame)
                    {
                        if (
                            _lastVideoKeyFrameRequestAt == DateTimeOffset.MinValue
                            || (now - _lastVideoKeyFrameRequestAt).TotalMilliseconds
                                >= keyFrameRequestIntervalMs
                        )
                        {
                            _lastVideoKeyFrameRequestAt = now;
                            activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                            _videoConsecutiveNoFrameDecodes = 0;
                            if (!isVp8Stream)
                            {
                                _isWaitingForVideoKeyFrame = true;
                            }
                            _lastVideoDecodeStatus = isVp8Stream
                                ? "decode gap; keyframe requested (non-blocking)"
                                : "decode gap; keyframe requested";
                        }
                    }
                    var statsSnapshot = activeWebRtcPeerConnectionService.GetVideoStatsSnapshot();
                    var renderStats = _openXrControllerInputService?.GetVideoRenderStatsSnapshot();
                    var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var renderIdleMs =
                        renderStats is null || renderStats.Value.LastRenderedAtUnixMs <= 0
                            ? -1
                            : nowUnixMs - renderStats.Value.LastRenderedAtUnixMs;
                    if (renderIdleMs < 0)
                    {
                        renderIdleMs = 0;
                    }
                    var syncStatus = _isWaitingForVideoKeyFrame
                        ? "sync=waiting-keyframe"
                        : "sync=ok";
                    _latestVideoStatus =
                        ConnectedVideoStatusPrefix
                        + _lastVideoDecodeStatus
                        + $" | mode={(shouldCatchupToLatest ? "catchup" : "ordered")}"
                        + $" | {syncStatus}"
                        + $" | rxFps={statsSnapshot.ReceivedFps:F1}"
                        + $" | rxKbps={statsSnapshot.ReceivedBitrateKbps:F0}"
                        + $" | q={statsSnapshot.QueueDepth}"
                        + $" | qDelayMs={statsSnapshot.LastLatencyMs}"
                        + $" | deqToDecMs={(decodeStartedAt - now).TotalMilliseconds:F0}"
                        + $" | decToNowMs={(DateTimeOffset.UtcNow - decodeCompletedAt).TotalMilliseconds:F0}"
                        + $" | renSeq={(renderStats?.LastRenderedSequence ?? 0)}"
                        + $" | renAgeRxMs={(renderStats?.LastRenderedAgeFromReceiveMs ?? 0)}"
                        + $" | renAgeDecMs={(renderStats?.LastRenderedAgeFromDecodeMs ?? 0)}"
                        + $" | renFail={(renderStats?.LastUploadFailureCode ?? 0)}"
                        + $" | renIdleMs={renderIdleMs}"
                        + $" | decMs={_lastDecodeElapsedMs}"
                        + EmulatorRouteHint;
                }

                MaybeLogVideoPipelineStats(logger);
                UpdateVideoLoopCheckpoint("loop-idle");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("Video decode loop failed.", ex);
                try
                {
                    await Task.Delay(20, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void MaybeLogVideoPipelineStats(AppLogger logger)
    {
        if (_webRtcPeerConnectionService is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        uint connectionId;
        uint framesObserved;
        uint decodeCalls;
        uint decodedFrames;
        string decodeStatus;
        long decodeElapsedMs;
        DateTimeOffset checkpointAt;
        string checkpointLabel;
        lock (_runtimeStateLock)
        {
            if (
                _lastVideoPipelineLogAt != DateTimeOffset.MinValue
                && (now - _lastVideoPipelineLogAt).TotalSeconds < 2
            )
            {
                return;
            }

            _lastVideoPipelineLogAt = now;
            connectionId = _videoConnectionId;
            framesObserved = _videoFramesObserved;
            decodeCalls = _videoDecodeCalls;
            decodedFrames = _videoDecodedFrames;
            decodeStatus = _lastVideoDecodeStatus;
            decodeElapsedMs = _lastDecodeElapsedMs;
            checkpointAt = _videoLoopCheckpointAt;
            checkpointLabel = _videoLoopCheckpointLabel;
        }

        var stats = _webRtcPeerConnectionService.GetVideoStatsSnapshot();
        var renderStats = _openXrControllerInputService?.GetVideoRenderStatsSnapshot();
        var checkpointAgeMs =
            checkpointAt == DateTimeOffset.MinValue
                ? -1
                : (long)(DateTimeOffset.UtcNow - checkpointAt).TotalMilliseconds;
        MaybeLogRenderStall(logger, stats, renderStats, decodedFrames);
        logger.Info(
            "Video pipeline stats: "
                + $"conn={connectionId} connected={stats.IsConnected} "
                + $"rxFrames={framesObserved} "
                + $"decodeCalls={decodeCalls} decodedFrames={decodedFrames} "
                + $"lastSeq={stats.LastSequence} lastPayload={stats.LastPayloadSize} "
                + $"queue={stats.QueueDepth} queueDelayMs={stats.LastLatencyMs} "
                + $"rxFps={stats.ReceivedFps:F1} rxKbps={stats.ReceivedBitrateKbps:F0} "
                + $"rawRtpPkts={stats.RawRtpPackets} lastRtpTs={stats.LastRtpTimestampUnixMs} pliReq={stats.PliRequests} "
                + $"renSeq={(renderStats?.LastRenderedSequence ?? 0)} "
                + $"renAgeRxMs={(renderStats?.LastRenderedAgeFromReceiveMs ?? 0)} "
                + $"renAgeDecMs={(renderStats?.LastRenderedAgeFromDecodeMs ?? 0)} "
                + $"renFail={(renderStats?.LastUploadFailureCode ?? 0)} "
                + $"decodeMs={decodeElapsedMs} "
                + $"loopCp={checkpointLabel} "
                + $"loopCpAgeMs={checkpointAgeMs} "
                + $"lastDecodeStatus={decodeStatus}"
        );
    }

    private void MaybeLogRenderStall(
        AppLogger logger,
        VideoStreamStats stats,
        OpenXrVideoRenderStats? renderStats,
        uint decodedFrames
    )
    {
        if (renderStats is null || !stats.IsConnected)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var renderedSequence = renderStats.Value.LastRenderedSequence;
        var decodedAdvanced = decodedFrames > _lastRenderProgressDecodedFrames;
        var renderAdvanced = renderedSequence > _lastRenderProgressSequence;

        if (renderAdvanced || _lastRenderProgressAt == DateTimeOffset.MinValue)
        {
            _lastRenderProgressSequence = renderedSequence;
            _lastRenderProgressDecodedFrames = decodedFrames;
            _lastRenderProgressAt = now;
            return;
        }

        if (!decodedAdvanced)
        {
            return;
        }

        var stalledMs = (long)(now - _lastRenderProgressAt).TotalMilliseconds;
        if (stalledMs < RenderStallLogThresholdMs)
        {
            return;
        }

        if (
            _lastRenderStallLogAt != DateTimeOffset.MinValue
            && (now - _lastRenderStallLogAt).TotalMilliseconds < RenderStallLogIntervalMs
        )
        {
            return;
        }

        _lastRenderStallLogAt = now;
        logger.Info(
            "Render stall suspect: "
                + $"renSeq={renderedSequence} decodedFrames={decodedFrames} stalledMs={stalledMs} "
                + $"renFail={renderStats.Value.LastUploadFailureCode} "
                + $"session={_latestOpenXrState.Status} "
                + $"rawRtpPkts={stats.RawRtpPackets} lastRtpTs={stats.LastRtpTimestampUnixMs}"
        );
    }

    private void UpdateVideoLoopCheckpoint(string label)
    {
        lock (_runtimeStateLock)
        {
            _videoLoopCheckpointAt = DateTimeOffset.UtcNow;
            _videoLoopCheckpointLabel = label;
        }
    }

    private void MaybeLogVideoLoopStall(AppLogger logger)
    {
        if (_webRtcPeerConnectionService is null)
        {
            return;
        }

        var stats = _webRtcPeerConnectionService.GetVideoStatsSnapshot();
        DateTimeOffset checkpointAt;
        string checkpointLabel;
        DateTimeOffset lastStallLogAt;
        lock (_runtimeStateLock)
        {
            checkpointAt = _videoLoopCheckpointAt;
            checkpointLabel = _videoLoopCheckpointLabel;
            lastStallLogAt = _lastVideoLoopStallLogAt;
        }

        if (!stats.IsConnected || checkpointAt == DateTimeOffset.MinValue)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var checkpointAgeMs = (long)(now - checkpointAt).TotalMilliseconds;
        if (checkpointAgeMs < VideoLoopStallLogThresholdMs)
        {
            return;
        }

        if (
            lastStallLogAt != DateTimeOffset.MinValue
            && (now - lastStallLogAt).TotalMilliseconds < VideoLoopStallLogIntervalMs
        )
        {
            return;
        }

        lock (_runtimeStateLock)
        {
            _lastVideoLoopStallLogAt = now;
        }

        var decodeDiag = _videoH264DecodeService?.GetDecodeDiagnosticSnapshot();
        logger.Info(
            "Video loop stall suspect: "
                + $"checkpoint={checkpointLabel} ageMs={checkpointAgeMs} "
                + $"rawRtpPkts={stats.RawRtpPackets} lastRtpTs={stats.LastRtpTimestampUnixMs} "
                + $"queue={stats.QueueDepth} lastSeq={stats.LastSequence} pliReq={stats.PliRequests} "
                + $"decodeStage={(decodeDiag?.Stage ?? "unknown")} "
                + $"decodeSeq={(decodeDiag?.Sequence ?? 0)} "
                + $"decodeStageAgeMs={(decodeDiag?.StageAgeMs ?? -1)}"
        );
    }

    private void ResetVideoPipelineMetrics()
    {
        lock (_runtimeStateLock)
        {
            _videoConnectionId = 0;
            _lastVideoPipelineLogAt = DateTimeOffset.MinValue;
            _lastVideoKeyFrameRequestAt = DateTimeOffset.MinValue;
            _videoFramesObserved = 0;
            _videoDecodeCalls = 0;
            _videoDecodedFrames = 0;
            _lastVideoDecodeStatus = "none";
            _lastDecodeElapsedMs = 0;
            _videoConsecutiveNoFrameDecodes = 0;
            _lastVideoDecodedAt = DateTimeOffset.MinValue;
            _isWaitingForVideoKeyFrame = true;
            _videoLoopCheckpointAt = DateTimeOffset.UtcNow;
            _videoLoopCheckpointLabel = "reset";
            _lastVideoLoopStallLogAt = DateTimeOffset.MinValue;
            _lastRenderProgressSequence = 0;
            _lastRenderProgressDecodedFrames = 0;
            _lastRenderProgressAt = DateTimeOffset.MinValue;
            _lastRenderStallLogAt = DateTimeOffset.MinValue;
            _latestVideoStatus = WaitingVideoStatus;
        }
    }
}
