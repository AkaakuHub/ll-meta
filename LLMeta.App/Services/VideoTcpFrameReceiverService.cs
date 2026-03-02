using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using LLMeta.App.Models;
using LLMeta.App.Utils;

namespace LLMeta.App.Services;

public sealed class VideoTcpFrameReceiverService : IDisposable
{
    private const uint ProtocolMagic = 0x544D564C; // "LVMT"
    private const byte ProtocolVersion = 1;
    private const byte KeyFrameFlagMask = 1 << 0;
    private const byte CodecConfigFlagMask = 1 << 1;
    private const byte ReservedFlagsMask = 0b1111_1100;
    private const int HeaderSize = 22;
    private const uint MaxPayloadLength = 4 * 1024 * 1024;

    private readonly AppLogger _logger;
    private readonly int _port;
    private readonly object _stateLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _isStarted;
    private int _connectionSequence;
    private readonly Queue<VideoFramePacket> _frameQueue = new();
    private const int MaxFrameQueueLength = 120;
    private VideoStreamStats _stats;
    private bool _loggedNegativeLatencyOnConnection;

    public VideoTcpFrameReceiverService(AppLogger logger, int port)
    {
        _logger = logger;
        _port = port;
        StatusText = "Video: not started";
    }

    public string StatusText { get; private set; }

    public void Start()
    {
        if (_isStarted)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start(1);
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _isStarted = true;
        UpdateStatus("Video: listening on 127.0.0.1:" + _port);
    }

    public bool TryGetLatestFrame(out VideoFramePacket frame)
    {
        lock (_stateLock)
        {
            if (_frameQueue.Count == 0)
            {
                frame = default;
                return false;
            }

            frame = _frameQueue.Dequeue();
            return true;
        }
    }

    public VideoStreamStats GetStatsSnapshot()
    {
        lock (_stateLock)
        {
            return _stats;
        }
    }

    public void Dispose()
    {
        if (!_isStarted)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.Error("Video receiver cancel failed during dispose.", ex);
        }

        try
        {
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.Error("Video receiver listener stop failed during dispose.", ex);
        }

        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.Error("Video receiver task wait failed during dispose.", ex);
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptTask = null;
        _isStarted = false;
        UpdateStatus("Video: stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                UpdateStatus("Video: client connected " + remote);
                _logger.Info("Video client connected: " + remote);
                SetConnected(true);
                var connectionId = unchecked((uint)Interlocked.Increment(ref _connectionSequence));
                BeginConnection(connectionId);
                await ReceiveLoopAsync(client, connectionId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Video receiver accept loop error.", ex);
                UpdateStatus("Video: error, waiting reconnect");
                await Task.Delay(500, cancellationToken);
            }
            finally
            {
                SetConnected(false);
                client?.Dispose();
            }
        }
    }

    private async Task ReceiveLoopAsync(
        TcpClient client,
        uint connectionId,
        CancellationToken cancellationToken
    )
    {
        using var stream = client.GetStream();
        var headerBuffer = new byte[HeaderSize];
        var loggedFirstPacket = false;

        while (!cancellationToken.IsCancellationRequested && client.Connected)
        {
            await ReadExactlyAsync(stream, headerBuffer, cancellationToken);
            var header = ParseHeader(headerBuffer);
            if (header.Magic != ProtocolMagic)
            {
                throw new InvalidDataException(
                    $"Video magic mismatch: 0x{header.Magic:X8} expected 0x{ProtocolMagic:X8}"
                );
            }

            if (header.Version != ProtocolVersion)
            {
                throw new InvalidDataException(
                    $"Video version mismatch: {header.Version} expected {ProtocolVersion}"
                );
            }

            if ((header.Flags & ReservedFlagsMask) != 0)
            {
                throw new InvalidDataException(
                    $"Video flags reserved bits must be zero: {header.Flags}"
                );
            }

            if (header.PayloadLength == 0 || header.PayloadLength > MaxPayloadLength)
            {
                throw new InvalidDataException(
                    $"Video payload length invalid: {header.PayloadLength}"
                );
            }

            var payload = new byte[checked((int)header.PayloadLength)];
            await ReadExactlyAsync(stream, payload, cancellationToken);
            ValidatePayloadAgainstFlags(payload, header.Flags);
            if (!loggedFirstPacket)
            {
                loggedFirstPacket = true;
                _logger.Info(
                    "Video first AU received: "
                        + $"conn={connectionId} seq={header.Sequence} flags={header.Flags} payload={header.PayloadLength}"
                );
            }
            PublishFrame(connectionId, header, payload);
        }
    }

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
                latencyMs
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

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken
            );
            if (read == 0)
            {
                throw new EndOfStreamException("Video stream ended while reading.");
            }

            offset += read;
        }
    }

    private static VideoHeader ParseHeader(byte[] headerBuffer)
    {
        var span = headerBuffer.AsSpan();
        return new VideoHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4)),
            span[4],
            span[5],
            BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(6, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(10, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(18, 4))
        );
    }

    private static void ValidatePayloadAgainstFlags(byte[] payload, byte flags)
    {
        var (hasSps, hasPps, hasIdr) = ParseAnnexBNalKinds(payload);
        var hasCodecConfigFlag = (flags & CodecConfigFlagMask) != 0;
        var isKeyFrameFlag = (flags & KeyFrameFlagMask) != 0;
        var hasCodecConfigPayload = hasSps && hasPps;

        if (hasCodecConfigFlag != hasCodecConfigPayload)
        {
            throw new InvalidDataException(
                $"Video hasCodecConfig mismatch. flag={hasCodecConfigFlag} payload={hasCodecConfigPayload}"
            );
        }

        if (isKeyFrameFlag != hasIdr)
        {
            throw new InvalidDataException(
                $"Video isKeyFrame mismatch. flag={isKeyFrameFlag} payload={hasIdr}"
            );
        }
    }

    private static (bool HasSps, bool HasPps, bool HasIdr) ParseAnnexBNalKinds(byte[] payload)
    {
        if (payload.Length < 5)
        {
            throw new InvalidDataException("Video payload is too short for Annex-B.");
        }

        if (!IsStartCodeAt(payload, 0))
        {
            throw new InvalidDataException(
                "Video payload must start with Annex-B start code 00 00 00 01."
            );
        }

        var hasSps = false;
        var hasPps = false;
        var hasIdr = false;
        var nalCount = 0;
        var offset = 0;

        while (offset < payload.Length)
        {
            if (!IsStartCodeAt(payload, offset))
            {
                throw new InvalidDataException(
                    $"Video payload has invalid Annex-B boundary at offset {offset}."
                );
            }

            var nalStart = offset + 4;
            var nextStartCode = FindNextStartCode(payload, nalStart);
            var nalEnd = nextStartCode >= 0 ? nextStartCode : payload.Length;
            if (nalStart >= nalEnd)
            {
                throw new InvalidDataException("Video payload contains an empty NAL unit.");
            }

            var nalType = payload[nalStart] & 0x1F;
            if (nalType == 7)
            {
                hasSps = true;
            }
            else if (nalType == 8)
            {
                hasPps = true;
            }
            else if (nalType == 5)
            {
                hasIdr = true;
            }

            nalCount++;
            if (nextStartCode < 0)
            {
                break;
            }

            offset = nextStartCode;
        }

        if (nalCount == 0)
        {
            throw new InvalidDataException("Video payload does not contain a valid NAL unit.");
        }

        return (hasSps, hasPps, hasIdr);
    }

    private static bool IsStartCodeAt(byte[] payload, int offset)
    {
        return offset + 4 <= payload.Length
            && payload[offset] == 0
            && payload[offset + 1] == 0
            && payload[offset + 2] == 0
            && payload[offset + 3] == 1;
    }

    private static int FindNextStartCode(byte[] payload, int searchStart)
    {
        for (var i = searchStart; i <= payload.Length - 4; i++)
        {
            if (IsStartCodeAt(payload, i))
            {
                return i;
            }
        }

        return -1;
    }

    private readonly record struct VideoHeader(
        uint Magic,
        byte Version,
        byte Flags,
        uint Sequence,
        ulong TimestampUnixMs,
        uint PayloadLength
    );
}
