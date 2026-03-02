using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LLMeta.App.Models;
using LLMeta.App.Utils;

namespace LLMeta.App.Services;

public sealed class WebRtcSignalingTcpServerService : IDisposable
{
    private readonly AppLogger _logger;
    private readonly int _port;
    private readonly object _writeLock = new();
    private readonly object _clientLock = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private TcpClient? _activeClient;
    private CancellationTokenSource? _activeClientCts;
    private Task? _activeClientTask;
    private StreamWriter? _writer;
    private bool _isStarted;

    public WebRtcSignalingTcpServerService(AppLogger logger, int port)
    {
        _logger = logger;
        _port = port;
        StatusText = "WebRTC signaling: not started";
    }

    public string StatusText { get; private set; }

    public event Action<WebRtcSignalingMessage>? MessageReceived;

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
        StatusText = $"WebRTC signaling: listening on 127.0.0.1:{_port}";
    }

    public bool TrySend(WebRtcSignalingMessage message)
    {
        var json = JsonSerializer.Serialize(message, _jsonOptions);
        StreamWriter? writer;
        lock (_writeLock)
        {
            writer = _writer;
        }

        if (writer is null)
        {
            return false;
        }

        try
        {
            lock (_writeLock)
            {
                writer.WriteLine(json);
                writer.Flush();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling send failed.", ex);
            return false;
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
            _logger.Error("WebRTC signaling cancel failed.", ex);
        }

        try
        {
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling listener stop failed.", ex);
        }

        try
        {
            _acceptLoopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling wait failed.", ex);
        }

        Task? activeClientTask = null;
        CancellationTokenSource? activeClientCts = null;
        TcpClient? activeClient = null;
        lock (_clientLock)
        {
            activeClientTask = _activeClientTask;
            activeClientCts = _activeClientCts;
            activeClient = _activeClient;
            _activeClientTask = null;
            _activeClientCts = null;
            _activeClient = null;
        }
        try
        {
            activeClientCts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling active client cancel failed.", ex);
        }
        try
        {
            activeClient?.Close();
            activeClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling active client dispose failed.", ex);
        }
        try
        {
            activeClientTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling active client wait failed.", ex);
        }
        activeClientCts?.Dispose();

        lock (_writeLock)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Error("WebRTC signaling writer dispose failed.", ex);
            }
            _writer = null;
        }

        _cts?.Dispose();
        _cts = null;
        _acceptLoopTask = null;
        _listener = null;
        _isStarted = false;
        StatusText = "WebRTC signaling: stopped";
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                _logger.Info($"WebRTC signaling client connected: {remote}");
                StatusText = $"WebRTC signaling: client connected {remote}";
                SwitchActiveClient(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("WebRTC signaling accept loop error.", ex);
                StatusText = "WebRTC signaling: error, waiting reconnect";
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    private void SwitchActiveClient(TcpClient client, CancellationToken serverToken)
    {
        Task? previousTask = null;
        CancellationTokenSource? previousCts = null;
        TcpClient? previousClient = null;
        CancellationTokenSource currentClientCts;
        lock (_clientLock)
        {
            previousTask = _activeClientTask;
            previousCts = _activeClientCts;
            previousClient = _activeClient;

            _activeClient = client;
            currentClientCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            _activeClientCts = currentClientCts;
            _activeClientTask = Task.Run(
                () => HandleClientLoopAsync(client, currentClientCts.Token),
                currentClientCts.Token
            );
        }

        try
        {
            previousCts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling previous client cancel failed.", ex);
        }

        try
        {
            previousClient?.Close();
            previousClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling previous client dispose failed.", ex);
        }

        previousCts?.Dispose();
        _ = previousTask;
    }

    private async Task HandleClientLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(client, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("WebRTC signaling client loop error.", ex);
        }
        finally
        {
            lock (_writeLock)
            {
                if (ReferenceEquals(_activeClient, client))
                {
                    try
                    {
                        _writer?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("WebRTC signaling active writer dispose failed.", ex);
                    }
                    _writer = null;
                }
            }
            try
            {
                client.Close();
                client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Error("WebRTC signaling client close failed.", ex);
            }

            lock (_clientLock)
            {
                if (ReferenceEquals(_activeClient, client))
                {
                    _activeClient = null;
                    _activeClientTask = null;
                    _activeClientCts?.Dispose();
                    _activeClientCts = null;
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        lock (_writeLock)
        {
            _writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            WebRtcSignalingMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WebRtcSignalingMessage>(line, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.Error("WebRTC signaling parse failed.", ex);
                continue;
            }

            if (message is null || string.IsNullOrWhiteSpace(message.Type))
            {
                continue;
            }

            MessageReceived?.Invoke(message);
        }
    }
}
