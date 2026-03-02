using System.Text.Json.Serialization;

namespace LLMeta.App.Models;

public sealed class WebRtcSignalingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("sdp")]
    public string? Sdp { get; init; }

    [JsonPropertyName("sdpType")]
    public string? SdpType { get; init; }

    [JsonPropertyName("candidate")]
    public string? Candidate { get; init; }

    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; init; }

    [JsonPropertyName("sdpMLineIndex")]
    public int? SdpMLineIndex { get; init; }
}
