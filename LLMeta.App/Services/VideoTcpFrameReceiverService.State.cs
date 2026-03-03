using LLMeta.App.Models;

namespace LLMeta.App.Services;

public sealed partial class VideoTcpFrameReceiverService
{
    private void PublishFrame(uint connectionId, VideoHeader header, byte[] payload)
    {
        lock (_stateLock)
        {
            var dropped = _stats.DroppedFrames;
            if (_stats.LastSequence != 0 && header.Sequence > _stats.LastSequence + 1)
            {
                dropped += header.Sequence - (_stats.LastSequence + 1);
            }

            var isKeyFrame = (header.Flags & KeyFrameFlagMask) != 0;
            var hasCodecConfig = (header.Flags & CodecConfigFlagMask) != 0;
            var packet = new VideoFramePacket(
                connectionId,
                header.Sequence,
                header.TimestampUnixMs,
                header.Flags,
                isKeyFrame,
                hasCodecConfig,
                "H264",
                payload
            );
            if (_frameQueue.Count >= MaxFrameQueueLength)
            {
                _ = _frameQueue.Dequeue();
                dropped += 1;
            }
            _frameQueue.Enqueue(packet);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rawLatencyMs = nowMs - (long)header.TimestampUnixMs;
            var latencyMs = rawLatencyMs < 0 ? 0 : rawLatencyMs;
            if (rawLatencyMs < 0 && !_loggedNegativeLatencyOnConnection)
            {
                _loggedNegativeLatencyOnConnection = true;
                _logger.Info(
                    "Video timestamp is ahead of receiver clock. "
                        + $"conn={connectionId} seq={header.Sequence} rawLatencyMs={rawLatencyMs} nowMs={nowMs} packetTs={header.TimestampUnixMs}"
                );
            }
            _stats = new VideoStreamStats(
                _stats.IsConnected,
                header.Sequence,
                header.TimestampUnixMs,
                dropped,
                payload.Length,
                latencyMs,
                (uint)_frameQueue.Count,
                0,
                0,
                0,
                0
            );
            UpdateStatus(
                "Video: connected"
                    + $" | magic=0x{header.Magic:X8}"
                    + $" | ver={header.Version}"
                    + $" | flags={header.Flags}"
                    + $" | seq={header.Sequence}"
                    + $" | payload={payload.Length}"
                    + $" | latencyMs={latencyMs}"
                    + $" | dropped={dropped}"
            );
        }
    }

    private void SetConnected(bool connected)
    {
        lock (_stateLock)
        {
            _stats = _stats with { IsConnected = connected };
            if (!connected && StatusText.StartsWith("Video: client connected"))
            {
                UpdateStatus("Video: client disconnected");
            }
        }
    }

    private void BeginConnection(uint connectionId)
    {
        lock (_stateLock)
        {
            _frameQueue.Clear();
            _stats = _stats with { LastSequence = 0, LastTimestampUnixMs = 0 };
            _loggedNegativeLatencyOnConnection = false;
        }
        _logger.Info("Video connection begin: conn=" + connectionId);
    }

    private void UpdateStatus(string status)
    {
        StatusText = status;
    }
}
