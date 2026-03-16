using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using LLMeta.App.Models;
using LLMeta.App.Utils;

namespace LLMeta.App.Services;

public sealed class WindowsInputTcpServerService : IDisposable
{
    private const int InputPayloadSize = 108;
    private static readonly TimeSpan InputTickInterval = TimeSpan.FromMilliseconds(11);

    private readonly AppLogger _logger;
    private readonly object _stateLock = new();
    private readonly object _clientLock = new();
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _lifecycleCts;
    private Task? _acceptLoopTask;
    private Task? _sendLoopTask;
    private TcpClient? _client;
    private NetworkStream? _clientStream;
    private OpenXrControllerState _latestInputState;
    private bool _isKeyboardDebugMode;
    private string _statusText;

    public WindowsInputTcpServerService(AppLogger logger, int port)
    {
        _logger = logger;
        _port = port;
        _latestInputState = default;
        _statusText = $"Input TCP: stopped ({port})";
    }

    public string StatusText
    {
        get
        {
            lock (_stateLock)
            {
                return _statusText;
            }
        }
    }

    public void Start()
    {
        if (_lifecycleCts is not null)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _lifecycleCts = new CancellationTokenSource();
        var token = _lifecycleCts.Token;
        lock (_stateLock)
        {
            _statusText = $"Input TCP: listening on {_port}";
        }

        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(token), token);
        _sendLoopTask = Task.Run(() => SendLoopAsync(token), token);
    }

    public void UpdateLatestInputState(OpenXrControllerState state, bool isKeyboardDebugMode)
    {
        lock (_stateLock)
        {
            _latestInputState = state;
            _isKeyboardDebugMode = isKeyboardDebugMode;
        }
    }

    public void Dispose()
    {
        var activeCts = _lifecycleCts;
        _lifecycleCts = null;
        if (activeCts is not null)
        {
            activeCts.Cancel();
        }

        try
        {
            CloseClient();
            _listener?.Stop();
        }
        catch { }

        try
        {
            Task.WaitAll(
                new[] { _acceptLoopTask, _sendLoopTask }
                    .Where(task => task is not null)
                    .Cast<Task>()
                    .ToArray(),
                TimeSpan.FromSeconds(2)
            );
        }
        catch { }

        activeCts?.Dispose();
        _acceptLoopTask = null;
        _sendLoopTask = null;
        _listener = null;
        lock (_stateLock)
        {
            _statusText = $"Input TCP: stopped ({_port})";
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                lock (_clientLock)
                {
                    CloseClient();
                    _client = client;
                    _client.NoDelay = true;
                    _clientStream = client.GetStream();
                }
                lock (_stateLock)
                {
                    _statusText = $"Input TCP: client connected ({_port})";
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Input TCP accept failed.", ex);
                lock (_stateLock)
                {
                    _statusText = $"Input TCP: accept error ({_port})";
                }
                try
                {
                    await Task.Delay(200, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        var payload = new byte[InputPayloadSize];
        using var timer = new PeriodicTimer(InputTickInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            InputFrame frame;
            lock (_stateLock)
            {
                frame = InputFrame.FromState(_latestInputState, _isKeyboardDebugMode);
            }

            NetworkStream? stream;
            lock (_clientLock)
            {
                stream = _clientStream;
            }

            if (stream is null)
            {
                continue;
            }

            BuildInputPayload(payload, frame);
            try
            {
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Input TCP send failed.", ex);
                CloseClient();
                lock (_stateLock)
                {
                    _statusText = $"Input TCP: send error, waiting reconnect ({_port})";
                }
            }
        }
    }

    private void CloseClient()
    {
        lock (_clientLock)
        {
            try
            {
                _clientStream?.Dispose();
            }
            catch { }

            try
            {
                _client?.Dispose();
            }
            catch { }

            _clientStream = null;
            _client = null;
        }
    }

    private static void BuildInputPayload(byte[] destination, InputFrame frame)
    {
        var span = destination.AsSpan();
        span.Clear();

        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(0, 4), frame.LeftStickX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(4, 4), frame.LeftStickY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(8, 4), frame.RightStickX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(12, 4), frame.RightStickY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(16, 4), frame.LeftTriggerValue);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(20, 4), frame.LeftGripValue);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(24, 4), frame.RightTriggerValue);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(28, 4), frame.RightGripValue);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(32, 4), frame.OrientationX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(36, 4), frame.OrientationY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(40, 4), frame.OrientationZ);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(44, 4), frame.OrientationW);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(48, 4), frame.HmdPositionX);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(52, 4), frame.HmdPositionY);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(56, 4), frame.HmdPositionZ);
        BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(60, 4),
            unchecked((int)frame.ButtonsBitMask)
        );
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(64, 4), frame.Flags);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(68, 4), frame.IpdMeters);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(72, 4), frame.HmdVerticalFovDegrees);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(76, 4), frame.LeftEyeAngleLeftRadians);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(80, 4), frame.LeftEyeAngleRightRadians);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(84, 4), frame.LeftEyeAngleUpRadians);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(88, 4), frame.LeftEyeAngleDownRadians);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(92, 4), frame.RightEyeAngleLeftRadians);
        BinaryPrimitives.WriteSingleLittleEndian(
            span.Slice(96, 4),
            frame.RightEyeAngleRightRadians
        );
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(100, 4), frame.RightEyeAngleUpRadians);
        BinaryPrimitives.WriteSingleLittleEndian(
            span.Slice(104, 4),
            frame.RightEyeAngleDownRadians
        );
    }

    private readonly record struct InputFrame(
        float LeftStickX,
        float LeftStickY,
        float RightStickX,
        float RightStickY,
        float LeftTriggerValue,
        float LeftGripValue,
        float RightTriggerValue,
        float RightGripValue,
        float OrientationX,
        float OrientationY,
        float OrientationZ,
        float OrientationW,
        float HmdPositionX,
        float HmdPositionY,
        float HmdPositionZ,
        uint ButtonsBitMask,
        int Flags,
        float IpdMeters,
        float HmdVerticalFovDegrees,
        float LeftEyeAngleLeftRadians,
        float LeftEyeAngleRightRadians,
        float LeftEyeAngleUpRadians,
        float LeftEyeAngleDownRadians,
        float RightEyeAngleLeftRadians,
        float RightEyeAngleRightRadians,
        float RightEyeAngleUpRadians,
        float RightEyeAngleDownRadians
    )
    {
        private const uint ButtonA = 1 << 0;
        private const uint ButtonB = 1 << 1;
        private const uint ButtonX = 1 << 2;
        private const uint ButtonY = 1 << 3;
        private const uint ButtonLeftStickClick = 1 << 4;
        private const uint ButtonRightStickClick = 1 << 5;

        public static InputFrame FromState(OpenXrControllerState state, bool isKeyboardDebugMode)
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

            var flags = 0;
            if (isKeyboardDebugMode)
            {
                flags |= 1;
            }

            return new InputFrame(
                state.LeftStickX,
                state.LeftStickY,
                state.RightStickX,
                state.RightStickY,
                state.LeftTriggerValue,
                state.LeftGripValue,
                state.RightTriggerValue,
                state.RightGripValue,
                state.HeadPose.OrientationX,
                state.HeadPose.OrientationY,
                state.HeadPose.OrientationZ,
                state.HeadPose.OrientationW,
                state.HeadPose.PositionX,
                state.HeadPose.PositionY,
                state.HeadPose.PositionZ,
                buttons,
                flags,
                state.IpdMeters,
                state.HmdVerticalFovDegrees,
                state.LeftEyeAngleLeftRadians,
                state.LeftEyeAngleRightRadians,
                state.LeftEyeAngleUpRadians,
                state.LeftEyeAngleDownRadians,
                state.RightEyeAngleLeftRadians,
                state.RightEyeAngleRightRadians,
                state.RightEyeAngleUpRadians,
                state.RightEyeAngleDownRadians
            );
        }
    }
}
