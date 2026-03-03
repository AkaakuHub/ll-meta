using LLMeta.App.Services;
using LLMeta.App.Utils;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace LLMeta.App;

public partial class App
{
    private async Task VideoDecodeLoopAsync(CancellationToken token, AppLogger logger)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var activeWebRtcPeerConnectionService = _webRtcPeerConnectionService;
                var activeVideoDecodeService = _videoH264DecodeService;
                if (
                    activeWebRtcPeerConnectionService is null
                    || activeVideoDecodeService is null
                    || !activeWebRtcPeerConnectionService.TryDequeueVideoFrame(
                        out var encodedPacket
                    )
                )
                {
                    MaybeLogVideoPipelineStats(logger);
                    await Task.Delay(1, token);
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
                            || (now - _lastVideoKeyFrameRequestAt).TotalMilliseconds >= 1000
                        )
                        {
                            _lastVideoKeyFrameRequestAt = now;
                            activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                        }
                        _lastVideoDecodeStatus = "waiting keyframe";
                    }
                    MaybeLogVideoPipelineStats(logger);
                    await Task.Delay(1, token);
                    continue;
                }

                lock (_runtimeStateLock)
                {
                    _videoDecodeCalls += 1;
                }
                var decodeStopwatch = Stopwatch.StartNew();
                var decodeStatus = activeVideoDecodeService.Decode(encodedPacket);
                decodeStopwatch.Stop();
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

                if (
                    _videoH264DecodeService is not null
                    && _videoH264DecodeService.TryGetLatestFrame(out var decodedFrame)
                    && _openXrControllerInputService is not null
                )
                {
                    lock (_runtimeStateLock)
                    {
                        _videoDecodedFrames += 1;
                        _lastVideoDecodedAt = now;
                    }
                    _openXrControllerInputService.SetLatestDecodedSbsFrame(decodedFrame);
                }

                lock (_runtimeStateLock)
                {
                    var stalledForMs =
                        _lastVideoDecodedAt == DateTimeOffset.MinValue
                            ? double.MaxValue
                            : (now - _lastVideoDecodedAt).TotalMilliseconds;
                    var shouldRequestKeyFrame =
                        _videoConsecutiveNoFrameDecodes >= 45 && stalledForMs >= 1200;
                    if (shouldRequestKeyFrame)
                    {
                        if (
                            _lastVideoKeyFrameRequestAt == DateTimeOffset.MinValue
                            || (now - _lastVideoKeyFrameRequestAt).TotalMilliseconds >= 1200
                        )
                        {
                            _lastVideoKeyFrameRequestAt = now;
                            activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                            _videoConsecutiveNoFrameDecodes = 0;
                            _isWaitingForVideoKeyFrame = true;
                        }
                    }
                    var statsSnapshot = activeWebRtcPeerConnectionService.GetVideoStatsSnapshot();
                    var syncStatus = _isWaitingForVideoKeyFrame
                        ? "sync=waiting-keyframe"
                        : "sync=ok";
                    _latestVideoStatus =
                        ConnectedVideoStatusPrefix
                        + _lastVideoDecodeStatus
                        + $" | {syncStatus}"
                        + $" | rxFps={statsSnapshot.ReceivedFps:F1}"
                        + $" | rxKbps={statsSnapshot.ReceivedBitrateKbps:F0}"
                        + $" | q={statsSnapshot.QueueDepth}"
                        + $" | qDelayMs={statsSnapshot.LastLatencyMs}"
                        + $" | decMs={_lastDecodeElapsedMs}"
                        + EmulatorRouteHint;
                }

                MaybeLogVideoPipelineStats(logger);
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
        }

        var stats = _webRtcPeerConnectionService.GetVideoStatsSnapshot();
        logger.Info(
            "Video pipeline stats: "
                + $"conn={connectionId} connected={stats.IsConnected} "
                + $"rxFrames={framesObserved} "
                + $"decodeCalls={decodeCalls} decodedFrames={decodedFrames} "
                + $"lastSeq={stats.LastSequence} lastPayload={stats.LastPayloadSize} "
                + $"queue={stats.QueueDepth} queueDelayMs={stats.LastLatencyMs} "
                + $"rxFps={stats.ReceivedFps:F1} rxKbps={stats.ReceivedBitrateKbps:F0} "
                + $"rawRtpPkts={stats.RawRtpPackets} pliReq={stats.PliRequests} "
                + $"decodeMs={decodeElapsedMs} "
                + $"lastDecodeStatus={decodeStatus}"
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
            _latestVideoStatus = WaitingVideoStatus;
        }
    }
}
