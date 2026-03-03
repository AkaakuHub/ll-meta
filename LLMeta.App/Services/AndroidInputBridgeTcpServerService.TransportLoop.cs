using System.Net.Sockets;

namespace LLMeta.App.Services;

public sealed partial class AndroidInputBridgeTcpServerService
{
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
}
