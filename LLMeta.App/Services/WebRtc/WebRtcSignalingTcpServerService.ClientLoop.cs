using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LLMeta.App.Models;

namespace LLMeta.App.Services;

public sealed partial class WebRtcSignalingTcpServerService
{
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
