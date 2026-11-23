using System.Globalization;
using System.IO;
using System.Text.Json;
using CRS.ExchangeRates;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.Services;

internal static class WorkerExchangeRatesGateway
{
    private static readonly BackgroundPipeClient Client = new();

    public static async Task<CentralParitySnapshot?> ReadAsync(int year, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var reply = await Client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), WorkerProtocol.GetAnnualExchangeRates,
                Payload: year.ToString(CultureInfo.InvariantCulture)), deadline.Token).ConfigureAwait(false);
            return reply is { Code: "Ok", Payload: { } json } ? JsonSerializer.Deserialize<CentralParitySnapshot>(json) : null;
        }
        catch (Exception error) when (error is BackgroundServiceException or InvalidDataException or JsonException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested) { return null; }
    }
}
