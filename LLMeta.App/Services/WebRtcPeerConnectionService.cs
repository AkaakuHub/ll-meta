using System.Net;
using System.Text.RegularExpressions;
using LLMeta.App.Models;
using LLMeta.App.Utils;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace LLMeta.App.Services;

public sealed class WebRtcPeerConnectionService : IDisposable
{
    private const int MaxVideoQueueLength = 8;
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
            _videoStats = _videoStats with { DroppedFrames = dropped };
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

    private static string SummarizeSdp(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            return "empty";
        }

        var lines = sdp.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var mLines = string.Join(
            " | ",
            lines.Where(line => line.StartsWith("m=", StringComparison.Ordinal))
        );
        var midLines = string.Join(
            " | ",
            lines.Where(line => line.StartsWith("a=mid:", StringComparison.Ordinal))
        );
        var bundleLine = lines.FirstOrDefault(line =>
            line.StartsWith("a=group:BUNDLE", StringComparison.Ordinal)
        );
        var rtcpMuxLines = string.Join(
            " | ",
            lines.Where(line => line.StartsWith("a=rtcp-mux", StringComparison.Ordinal))
        );
        return $"len={sdp.Length}; bundle={bundleLine}; mids={midLines}; m={mLines}; rtcpMux={rtcpMuxLines}";
    }

    private void SetCandidateRoutingFromOffer(string offerSdp)
    {
        var lines = offerSdp.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var mids = lines
            .Where(line => line.StartsWith("a=mid:", StringComparison.Ordinal))
            .Select(line => line.Substring("a=mid:".Length).Trim())
            .Where(mid => !string.IsNullOrWhiteSpace(mid))
            .ToList();
        var bundleLine = lines.FirstOrDefault(line =>
            line.StartsWith("a=group:BUNDLE", StringComparison.Ordinal)
        );
        if (bundleLine is null)
        {
            lock (_stateLock)
            {
                _candidateMid = "0";
                _candidateMLineIndex = 0;
            }
            return;
        }

        var tokens = bundleLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return;
        }

        var preferredMid = tokens[1];
        var preferredIndex = mids.FindIndex(mid =>
            string.Equals(mid, preferredMid, StringComparison.Ordinal)
        );
        if (preferredIndex < 0)
        {
            preferredIndex = 0;
        }

        lock (_stateLock)
        {
            _candidateMid = preferredMid;
            _candidateMLineIndex = (ushort)preferredIndex;
        }
        _logger.Info(
            $"WebRTC candidate routing selected: mid={_candidateMid} mline={_candidateMLineIndex}"
        );
    }

    private void HandleRemoteIceCandidate(WebRtcSignalingMessage message)
    {
        if (_peerConnection is null)
        {
            return;
        }

        var candidate = message.Candidate;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var normalizedCandidate = NormalizeRemoteIceCandidate(candidate);
        var candidateInit = new RTCIceCandidateInit
        {
            candidate = normalizedCandidate,
            sdpMid = message.SdpMid,
            sdpMLineIndex = (ushort)(message.SdpMLineIndex ?? 0),
        };
        _peerConnection.addIceCandidate(candidateInit);
        _logger.Info(
            $"WebRTC remote ICE candidate added: mid={candidateInit.sdpMid} mline={candidateInit.sdpMLineIndex} candidate={candidateInit.candidate}"
        );
    }

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
            _videoStats = new VideoStreamStats(
                IsConnected: false,
                LastSequence: 0,
                LastTimestampUnixMs: 0,
                DroppedFrames: 0,
                LastPayloadSize: 0,
                LastLatencyMs: 0
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
                if (_rawVideoRtpPackets <= 5 || _rawVideoRtpPackets % 120 == 0)
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

    private void HandleVideoFrame(byte[] payload, string codecName)
    {
        lock (_stateLock)
        {
            var sequence = _lastVideoSequence + 1;
            _lastVideoSequence = sequence;
            _currentVideoCodecName = codecName;

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
                IsKeyFrame: false,
                HasCodecConfig: false,
                CodecName: _currentVideoCodecName,
                Payload: payload
            );
            _videoFrameQueue.Enqueue(packet);

            _videoStats = new VideoStreamStats(
                IsConnected: _videoStats.IsConnected,
                LastSequence: sequence,
                LastTimestampUnixMs: timestampUnixMs,
                DroppedFrames: dropped,
                LastPayloadSize: payload.Length,
                LastLatencyMs: 0
            );
        }
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

    private static string NormalizeLocalIceCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        return ReplaceLoopbackIp(candidate, "10.0.2.2");
    }

    private static string NormalizeRemoteIceCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        return candidate;
    }

    private static string ReplaceLoopbackIp(string candidate, string replacementIp)
    {
        var match = CandidateIpRegex.Match(candidate);
        if (!match.Success)
        {
            return candidate;
        }

        var ip = match.Groups[2].Value;
        if (ip != "127.0.0.1" && ip != "::1")
        {
            return candidate;
        }

        return match.Groups[1].Value + replacementIp + match.Groups[3].Value;
    }
}
