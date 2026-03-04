using LLMeta.App.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace LLMeta.App.Services;

public sealed partial class WebRtcPeerConnectionService
{
    private void EnsurePeerConnection()
    {
        if (_peerConnection is not null)
        {
            return;
        }

        lock (_stateLock)
        {
            _videoConnectionId = unchecked(
                (uint)Interlocked.Increment(ref _videoConnectionSequence)
            );
            _videoFrameQueue.Clear();
            _lastVideoSequence = 0;
            _remoteVideoSsrc = 0;
            _rawVideoRtpPackets = 0;
            _currentVideoCodecName = "unknown";
            _candidateMid = "0";
            _candidateMLineIndex = 0;
            _receiveWindowStartedAt = DateTimeOffset.MinValue;
            _receiveWindowFrames = 0;
            _receiveWindowBytes = 0;
            _receiveFps = 0;
            _receiveBitrateKbps = 0;
            _pliRequests = 0;
            _videoStats = new VideoStreamStats(
                IsConnected: false,
                LastSequence: 0,
                LastTimestampUnixMs: 0,
                DroppedFrames: 0,
                LastPayloadSize: 0,
                LastLatencyMs: 0,
                QueueDepth: 0,
                RawRtpPackets: 0,
                ReceivedFps: 0,
                ReceivedBitrateKbps: 0,
                PliRequests: 0
            );
        }

        var config = new RTCConfiguration
        {
            X_ICEIncludeAllInterfaceAddresses = true,
            iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }],
        };
        _peerConnection = new RTCPeerConnection(config);
        _localVideoReceiveTrack = new MediaStreamTrack(
            new List<VideoFormat> { new(VideoCodecsEnum.VP8, 96, 90000, string.Empty) },
            MediaStreamStatusEnum.RecvOnly
        );
        _peerConnection.addTrack(_localVideoReceiveTrack);
        _logger.Info("WebRTC local video receive track registered.");
        _peerConnection.onicecandidate += candidate =>
        {
            if (candidate is null)
            {
                return;
            }
            _logger.Info(
                $"WebRTC local ICE candidate: mid={candidate.sdpMid} mline={candidate.sdpMLineIndex} candidate={candidate.candidate}"
            );
            var normalizedCandidate = NormalizeLocalIceCandidate(candidate.candidate);
            string outboundMid;
            ushort outboundMLineIndex;
            if (!string.IsNullOrWhiteSpace(candidate.sdpMid))
            {
                outboundMid = candidate.sdpMid;
                outboundMLineIndex = candidate.sdpMLineIndex;
            }
            else
            {
                lock (_stateLock)
                {
                    outboundMid = _candidateMid;
                    outboundMLineIndex = _candidateMLineIndex;
                }
            }
            OutboundSignalingMessage?.Invoke(
                new WebRtcSignalingMessage
                {
                    Type = "ice-candidate",
                    Candidate = normalizedCandidate,
                    SdpMid = outboundMid,
                    SdpMLineIndex = outboundMLineIndex,
                }
            );
        };
        _peerConnection.onconnectionstatechange += state =>
        {
            var connected =
                state == RTCPeerConnectionState.connected
                || state == RTCPeerConnectionState.connecting;
            lock (_stateLock)
            {
                _videoStats = _videoStats with { IsConnected = connected };
            }
            _logger.Info($"WebRTC peer connection state: {state}");
        };
        _peerConnection.oniceconnectionstatechange += state =>
        {
            _logger.Info($"WebRTC ICE connection state: {state}");
        };
        _peerConnection.ondatachannel += dataChannel =>
        {
            _logger.Info($"WebRTC data channel opened: {dataChannel.label}");
            dataChannel.onmessage += (_, _, payload) =>
            {
                if (payload is null || payload.Length == 0)
                {
                    return;
                }

                _logger.Info(
                    $"WebRTC data message: channel={dataChannel.label} size={payload.Length}"
                );
            };
        };

        _peerConnection.OnRtpPacketReceived += (_, mediaType, rtpPacket) =>
        {
            if (mediaType != SDPMediaTypesEnum.video || rtpPacket is null)
            {
                return;
            }

            lock (_stateLock)
            {
                _remoteVideoSsrc = rtpPacket.Header.SyncSource;
                _rawVideoRtpPackets += 1;
                if (_rawVideoRtpPackets <= 5 || _rawVideoRtpPackets % 3000 == 0)
                {
                    _logger.Info(
                        $"WebRTC raw RTP video packets: count={_rawVideoRtpPackets} pt={rtpPacket.Header.PayloadType} ssrc={_remoteVideoSsrc}"
                    );
                }
            }
        };

        _peerConnection.OnVideoFrameReceived += (_, _, payload, format) =>
        {
            if (payload is null || payload.Length == 0)
            {
                return;
            }

            var codecName = string.IsNullOrWhiteSpace(format.FormatName)
                ? "unknown"
                : format.FormatName;
            HandleVideoFrame(payload, codecName);
        };
    }

    private void ClosePeerConnection()
    {
        if (_peerConnection is null)
        {
            return;
        }

        _peerConnection.close();
        _peerConnection.Dispose();
        _peerConnection = null;
        _localVideoReceiveTrack = null;
    }
}
