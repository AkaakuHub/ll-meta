using LLMeta.App.Models;
using SIPSorcery.Net;

namespace LLMeta.App.Services;

public sealed partial class WebRtcPeerConnectionService
{
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

        var tokens = bundleLine.Split(" ", StringSplitOptions.RemoveEmptyEntries);
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
