using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using LLMeta.App.Models;
using LLMeta.App.Utils;

namespace LLMeta.App.Services;

public sealed class AndroidInputBridgeTcpServerService : IDisposable
{
    private const int HeaderSize = 8;
    private const int BodySize = 72;
    private const int PacketSize = HeaderSize + BodySize;
    private const uint Magic = 0x4C4D4554; // "LMET"
    private const byte Version = 1;

    private readonly AppLogger _logger;
    private readonly int _port;
    private readonly object _stateLock = new();
    private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(1.0 / 90.0);

    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private TcpListener? _listener;
    private bool _isStarted;
    private uint _sequence;
    private BridgeFrame _latestFrame;

    public AndroidInputBridgeTcpServerService(AppLogger logger, int port)
    {
        _logger = logger;
        _port = port;
        _latestFrame = BridgeFrame.Empty;
        StatusText = "Bridge: not started";
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
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _isStarted = true;
        UpdateStatus("Bridge: listening on 127.0.0.1:" + _port);
    }

    public void UpdateLatestState(OpenXrControllerState state, bool isKeyboardDebugMode)
    {
        lock (_stateLock)
        {
            _latestFrame = BridgeFrame.FromState(state, isKeyboardDebugMode);
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
            _logger.Error("Bridge cancel failed during dispose.", ex);
        }

        try
        {
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.Error("Bridge listener stop failed during dispose.", ex);
        }

        try
        {
            _acceptLoopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.Error("Bridge accept task wait failed during dispose.", ex);
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptLoopTask = null;
        _isStarted = false;
        UpdateStatus("Bridge: stopped");
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
                var remoteEndPointText = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                UpdateStatus("Bridge: client connected " + remoteEndPointText);
                _logger.Info("Bridge client connected: " + remoteEndPointText);
                await SendLoopAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Bridge accept/send loop error.", ex);
                UpdateStatus("Bridge: error, waiting reconnect");
                await Task.Delay(500, cancellationToken);
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    private async Task SendLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_tickInterval);
        using var stream = client.GetStream();
        var packet = new byte[PacketSize];

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!client.Connected)
            {
                UpdateStatus("Bridge: client disconnected");
                return;
            }

            BridgeFrame snapshot;
            lock (_stateLock)
            {
                snapshot = _latestFrame;
            }

            BuildPacket(packet, snapshot, _sequence++);
            await stream.WriteAsync(packet, cancellationToken);
        }
    }

    private void BuildPacket(byte[] destination, BridgeFrame frame, uint sequence)
    {
        var span = destination.AsSpan();
        span.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        span[4] = Version;
        span[5] = frame.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), BodySize);

        var body = span.Slice(HeaderSize, BodySize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), frame.TimestampUnixMs);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(12, 4), frame.LeftStickX);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(16, 4), frame.LeftStickY);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(20, 4), frame.RightStickX);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(24, 4), frame.RightStickY);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(28, 4), frame.LeftTriggerValue);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(32, 4), frame.LeftGripValue);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(36, 4), frame.RightTriggerValue);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(40, 4), frame.RightGripValue);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(44, 4), frame.YawRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(48, 4), frame.PitchRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(52, 4), frame.RollRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(56, 4), frame.HmdPositionX);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(60, 4), frame.HmdPositionY);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(64, 4), frame.HmdPositionZ);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(68, 4), frame.ButtonsBitMask);
    }

    private void UpdateStatus(string statusText)
    {
        StatusText = statusText;
    }

    private readonly record struct BridgeFrame(
        ulong TimestampUnixMs,
        float LeftStickX,
        float LeftStickY,
        float RightStickX,
        float RightStickY,
        float LeftTriggerValue,
        float LeftGripValue,
        float RightTriggerValue,
        float RightGripValue,
        float YawRadians,
        float PitchRadians,
        float RollRadians,
        float HmdPositionX,
        float HmdPositionY,
        float HmdPositionZ,
        uint ButtonsBitMask,
        byte Flags
    )
    {
        private const float DegToRad = 0.0174532925f;
        private const uint ButtonA = 1 << 0;
        private const uint ButtonB = 1 << 1;
        private const uint ButtonX = 1 << 2;
        private const uint ButtonY = 1 << 3;
        private const uint ButtonLeftStickClick = 1 << 4;
        private const uint ButtonRightStickClick = 1 << 5;

        public static BridgeFrame Empty => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        public static BridgeFrame FromState(OpenXrControllerState state, bool isKeyboardDebugMode)
        {
            var buttons = 0u;
            if (state.RightAPressed)
            {
                buttons |= ButtonA;
            }

            if (state.RightBPressed)
            {
                buttons |= ButtonB;
            }

            if (state.LeftXPressed)
            {
                buttons |= ButtonX;
            }

            if (state.LeftYPressed)
            {
                buttons |= ButtonY;
            }

            if (state.LeftStickClickPressed)
            {
                buttons |= ButtonLeftStickClick;
            }

            if (state.RightStickClickPressed)
            {
                buttons |= ButtonRightStickClick;
            }

            var flags = (byte)0;
            if (isKeyboardDebugMode)
            {
                flags |= 1;
            }

            return new BridgeFrame(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                state.LeftStickX,
                state.LeftStickY,
                state.RightStickX,
                state.RightStickY,
                state.LeftTriggerValue,
                state.LeftGripValue,
                state.RightTriggerValue,
                state.RightGripValue,
                state.HeadPose.YawDegrees * DegToRad,
                state.HeadPose.PitchDegrees * DegToRad,
                state.HeadPose.RollDegrees * DegToRad,
                state.HeadPose.PositionX,
                state.HeadPose.PositionY,
                state.HeadPose.PositionZ,
                buttons,
                flags
            );
        }
    }
}
