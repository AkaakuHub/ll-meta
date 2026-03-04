using System.IO;
using System.Net;
using System.Net.Sockets;
using LLMeta.App.Models;
using LLMeta.App.Utils;

namespace LLMeta.App.Services;

public sealed partial class VideoTcpFrameReceiverService : IDisposable
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
}
