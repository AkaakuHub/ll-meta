using LLMeta.App.Models;

namespace LLMeta.App.Services;

public sealed partial class WebRtcPeerConnectionService
{
    private void HandleVideoFrame(byte[] payload, string codecName)
    {
        lock (_stateLock)
        {
            var sequence = _lastVideoSequence + 1;
            _lastVideoSequence = sequence;
            _currentVideoCodecName = codecName;
            var isKeyFrame = IsKeyFrame(payload, _currentVideoCodecName);

            var dropped = _videoStats.DroppedFrames;
            if (_videoFrameQueue.Count >= MaxVideoQueueLength)
            {
                _videoFrameQueue.Dequeue();
                dropped += 1;
            }

            var timestampUnixMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var packet = new VideoFramePacket(
                ConnectionId: _videoConnectionId,
                Sequence: sequence,
                TimestampUnixMs: timestampUnixMs,
                Flags: 0,
                IsKeyFrame: isKeyFrame,
                HasCodecConfig: false,
                CodecName: _currentVideoCodecName,
                Payload: payload
            );
            _videoFrameQueue.Enqueue(packet);
            var now = DateTimeOffset.UtcNow;
            if (_receiveWindowStartedAt == DateTimeOffset.MinValue)
            {
                _receiveWindowStartedAt = now;
            }
            _receiveWindowFrames += 1;
            _receiveWindowBytes += (ulong)payload.Length;
            var windowSeconds = (now - _receiveWindowStartedAt).TotalSeconds;
            if (windowSeconds >= 1.0)
            {
                _receiveFps = _receiveWindowFrames / windowSeconds;
                _receiveBitrateKbps = (_receiveWindowBytes * 8.0) / 1000.0 / windowSeconds;
                _receiveWindowFrames = 0;
                _receiveWindowBytes = 0;
                _receiveWindowStartedAt = now;
            }

            _videoStats = new VideoStreamStats(
                IsConnected: _videoStats.IsConnected,
                LastSequence: sequence,
                LastTimestampUnixMs: timestampUnixMs,
                DroppedFrames: dropped,
                LastPayloadSize: payload.Length,
                LastLatencyMs: _videoStats.LastLatencyMs,
                QueueDepth: (uint)_videoFrameQueue.Count,
                RawRtpPackets: _rawVideoRtpPackets,
                ReceivedFps: _receiveFps,
                ReceivedBitrateKbps: _receiveBitrateKbps,
                PliRequests: _pliRequests
            );
        }
    }

    private static bool IsKeyFrame(byte[] payload, string codecName)
    {
        if (payload.Length == 0)
        {
            return false;
        }

        if (!codecName.Equals("VP8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var vp8FrameTag = payload[0];
        return (vp8FrameTag & 0x01) == 0;
    }
}
