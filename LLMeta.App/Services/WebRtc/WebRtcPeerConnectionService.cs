using System.Net;
using System.Text.RegularExpressions;
using LLMeta.App.Models;
using LLMeta.App.Utils;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace LLMeta.App.Services;

public sealed partial class WebRtcPeerConnectionService : IDisposable
{
    private const int MaxVideoQueueLength = 4;
    private static readonly Regex CandidateIpRegex = new(
        @"^(candidate:\S+\s+\d+\s+(?:udp|tcp)\s+\d+\s+)(\S+)(\s+\d+\s+typ\s+\S+.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    private readonly AppLogger _logger;
    private readonly object _stateLock = new();
    private readonly Queue<VideoFramePacket> _videoFrameQueue = new();
    private RTCPeerConnection? _peerConnection;
    private MediaStreamTrack? _localVideoReceiveTrack;
    private uint _videoConnectionId;
    private int _videoConnectionSequence;
    private VideoStreamStats _videoStats;
    private uint _lastVideoSequence;
    private uint _remoteVideoSsrc;
    private ulong _rawVideoRtpPackets;
    private string _currentVideoCodecName = "unknown";
    private string _candidateMid = "0";
    private ushort _candidateMLineIndex;
    private DateTimeOffset _receiveWindowStartedAt = DateTimeOffset.MinValue;
    private uint _receiveWindowFrames;
    private ulong _receiveWindowBytes;
    private double _receiveFps;
    private double _receiveBitrateKbps;
    private uint _pliRequests;

    public WebRtcPeerConnectionService(AppLogger logger)
    {
        _logger = logger;
    }

    public event Action<WebRtcSignalingMessage>? OutboundSignalingMessage;

    public Task HandleSignalingMessageAsync(WebRtcSignalingMessage message)
    {
        if (message.Type == "offer")
        {
            HandleOffer(message);
            return Task.CompletedTask;
        }

        if (message.Type == "ice-candidate")
        {
            HandleRemoteIceCandidate(message);
        }

        return Task.CompletedTask;
    }

    public bool TryDequeueVideoFrame(out VideoFramePacket frame)
    {
        lock (_stateLock)
        {
            if (_videoFrameQueue.Count == 0)
            {
                frame = default;
                return false;
            }

            frame = _videoFrameQueue.Dequeue();
            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var latencyMs = nowUnixMs - (long)frame.TimestampUnixMs;
            if (latencyMs < 0)
            {
                latencyMs = 0;
            }
            _videoStats = _videoStats with
            {
                LastLatencyMs = latencyMs,
                QueueDepth = (uint)_videoFrameQueue.Count,
            };
            return true;
        }
    }

    public bool TryDequeueLatestVideoFrame(out VideoFramePacket frame)
    {
        lock (_stateLock)
        {
            if (_videoFrameQueue.Count == 0)
            {
                frame = default;
                return false;
            }

            frame = _videoFrameQueue.Dequeue();
            var dropped = _videoStats.DroppedFrames;
            while (_videoFrameQueue.Count > 0)
            {
                frame = _videoFrameQueue.Dequeue();
                dropped += 1;
            }

            var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var latencyMs = nowUnixMs - (long)frame.TimestampUnixMs;
            if (latencyMs < 0)
            {
                latencyMs = 0;
            }
            _videoStats = _videoStats with
            {
                DroppedFrames = dropped,
                LastLatencyMs = latencyMs,
                QueueDepth = 0,
            };
            return true;
        }
    }

    public VideoStreamStats GetVideoStatsSnapshot()
    {
        lock (_stateLock)
        {
            return _videoStats;
        }
    }

    public void RequestVideoKeyFrame()
    {
        RTCPeerConnection? activePeerConnection;
        uint remoteVideoSsrc;
        lock (_stateLock)
        {
            activePeerConnection = _peerConnection;
            remoteVideoSsrc = _remoteVideoSsrc;
        }

        if (activePeerConnection is null || remoteVideoSsrc == 0)
        {
            return;
        }

        var senderSsrc = activePeerConnection.VideoRtcpSession?.Ssrc ?? 0;
        var pli = new RTCPFeedback(senderSsrc, remoteVideoSsrc, PSFBFeedbackTypesEnum.PLI);
        activePeerConnection.SendRtcpFeedback(SDPMediaTypesEnum.video, pli);
        lock (_stateLock)
        {
            _pliRequests += 1;
            _videoStats = _videoStats with { PliRequests = _pliRequests };
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _videoFrameQueue.Clear();
            _videoStats = _videoStats with { IsConnected = false };
            _remoteVideoSsrc = 0;
            _rawVideoRtpPackets = 0;
            _currentVideoCodecName = "unknown";
            _receiveWindowStartedAt = DateTimeOffset.MinValue;
            _receiveWindowFrames = 0;
            _receiveWindowBytes = 0;
            _receiveFps = 0;
            _receiveBitrateKbps = 0;
            _pliRequests = 0;
        }
        ClosePeerConnection();
    }

    private void HandleOffer(WebRtcSignalingMessage message)
    {
        var sdp = message.Sdp;
        if (string.IsNullOrWhiteSpace(sdp))
        {
            _logger.Info("WebRTC offer ignored: empty SDP.");
            return;
        }

        ClosePeerConnection();
        EnsurePeerConnection();
        if (_peerConnection is null)
        {
            return;
        }

        var remote = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp };
        SetCandidateRoutingFromOffer(sdp);
        _logger.Info($"WebRTC offer SDP summary: {SummarizeSdp(sdp)}");
        var setRemoteResult = _peerConnection.setRemoteDescription(remote);
        if (setRemoteResult != SetDescriptionResultEnum.OK)
        {
            _logger.Info($"WebRTC setRemoteDescription failed: {setRemoteResult}");
            return;
        }
        var answer = _peerConnection.createAnswer(null);
        if (string.IsNullOrWhiteSpace(answer.sdp))
        {
            var generatedAnswer = _peerConnection.CreateAnswer(IPAddress.Loopback);
            var generatedAnswerSdp = generatedAnswer?.ToString();
            if (!string.IsNullOrWhiteSpace(generatedAnswerSdp))
            {
                answer.sdp = generatedAnswerSdp;
            }
        }
        if (string.IsNullOrWhiteSpace(answer.sdp))
        {
            _logger.Info("WebRTC answer generation failed: empty SDP.");
            return;
        }
        _peerConnection.setLocalDescription(answer);
        _logger.Info($"WebRTC answer SDP summary: {SummarizeSdp(answer.sdp)}");
        OutboundSignalingMessage?.Invoke(
            new WebRtcSignalingMessage
            {
                Type = "answer",
                SdpType = "answer",
                Sdp = answer.sdp,
            }
        );
        _logger.Info("WebRTC answer created and sent.");
    }
}
