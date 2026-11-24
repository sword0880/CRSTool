using System.Globalization;
using System.IO;
using System.Text.Json;
using CRS.ExchangeRates;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.Services;

internal static class WorkerExchangeRatesGateway
{
    private static readonly BackgroundPipeClient Client = new();

    public static Task<CentralParitySnapshot?> ReadAsync(int year, CancellationToken cancellation) =>
        ReadSnapshotAsync(WorkerProtocol.GetAnnualExchangeRates, year, TimeSpan.FromSeconds(3), cancellation);

    public static Task<CentralParitySnapshot?> RefreshAsync(int year, CancellationToken cancellation) =>
        ReadSnapshotAsync(WorkerProtocol.RefreshAnnualExchangeRates, year, TimeSpan.FromSeconds(28), cancellation);

    private static async Task<CentralParitySnapshot?> ReadSnapshotAsync(string command, int year,
        TimeSpan timeout, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try
        {
            var reply = await Client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), command,
                Payload: year.ToString(CultureInfo.InvariantCulture)), deadline.Token).ConfigureAwait(false);
            return reply is { Code: "Ok", Payload: { } json }
                ? JsonSerializer.Deserialize<CentralParitySnapshot>(json) : null;
        }
        catch (Exception error) when (error is BackgroundServiceException or InvalidDataException or JsonException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested) { return null; }
    }

    public static Task<CentralParitySnapshot?> ReadLegacySnapshotAsync(int year, CancellationToken cancellation) =>
        ReadSnapshotAsync(WorkerProtocol.GetLegacyAnnualExchangeRateSnapshot, year, TimeSpan.FromSeconds(3), cancellation);

    public static async Task<LegacyAnnualExchangeRateRecord[]> ReadLegacyAsync(int year, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var reply = await Client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                WorkerProtocol.GetLegacyAnnualExchangeRates, Payload: year.ToString(CultureInfo.InvariantCulture)),
                deadline.Token).ConfigureAwait(false);
            return reply is { Code: "Ok", Payload: { } json }
                ? JsonSerializer.Deserialize<LegacyAnnualExchangeRateRecord[]>(json) ?? [] : [];
        }
        catch (Exception error) when (error is BackgroundServiceException or InvalidDataException or JsonException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested) { return []; }
    }

    public static async Task<bool> ImportLegacyAsync(CentralParitySnapshot snapshot, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var payload = JsonSerializer.Serialize(snapshot);
            var reply = await Client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                WorkerProtocol.ImportAnnualExchangeRateSnapshot, Payload: payload), deadline.Token).ConfigureAwait(false);
            return reply.Code == "Ok";
        }
        catch (Exception error) when (error is BackgroundServiceException or InvalidDataException or JsonException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested) { return false; }
    }
}
