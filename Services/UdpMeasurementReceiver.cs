using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TrainingSystem.PC.Models;

namespace TrainingSystem.PC.Services;

public sealed class UdpMeasurementReceiver : IAsyncDisposable
{
    private UdpClient? _client;
    private CancellationTokenSource? _cts;

    public bool IsListening => _client is not null;

    public async Task StartAsync(int port, Func<SensorMessage, Task> onMessage)
    {
        if (_client is not null)
            throw new InvalidOperationException("Receiver is already listening.");

        _client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _cts = new CancellationTokenSource();
        _ = ReceiveLoopAsync(onMessage, _cts.Token);
        await Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(Func<SensorMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        UdpClient? client = _client;
        if (client is null) return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await client.ReceiveAsync(cancellationToken);
                try
                {
                    var message = JsonSerializer.Deserialize<SensorMessage>(result.Buffer,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (message is not null)
                        // Pi'en sender quat/pos/force_n i undergrupper. Normalize() lægger
                        // dem over på de felter, resten af programmet kender.
                        await onMessage(message.Normalize());
                }
                catch (JsonException)
                {
                    // Ignore malformed packets; the UI remains available.
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task StopAsync()
    {
        if (_client is null) return;
        _cts?.Cancel();
        _client.Dispose();
        _client = null;
        _cts?.Dispose();
        _cts = null;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
