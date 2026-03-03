using System.Net;
using System.Net.Sockets;
using LLMeta.App.Models;
using LLMeta.App.Utils;

namespace LLMeta.App.Services;

public sealed partial class AndroidInputBridgeTcpServerService : IDisposable
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

    private void UpdateStatus(string statusText)
    {
        StatusText = statusText;
    }
}
