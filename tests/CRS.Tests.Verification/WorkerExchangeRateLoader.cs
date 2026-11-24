using System.Globalization;
using System.IO;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.ExchangeRates;
using CRS.Infrastructure;
using CRS.Worker.Ipc;

/// <summary>真实样本验收只从 Worker SQLCipher 读取汇率，不再接受桌面 JSON 配置目录。</summary>
internal static class WorkerExchangeRateLoader
{
    private static readonly BackgroundPipeClient Client = new();

    internal static IExchangeRateProvider Load(int year)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var provider = new OfficialAnnualExchangeRates(
            (value, token) => ReadSnapshotAsync(WorkerProtocol.GetAnnualExchangeRates, value, token),
            (value, token) => ReadSnapshotAsync(WorkerProtocol.RefreshAnnualExchangeRates, value, token),
            (value, token) => ReadSnapshotAsync(WorkerProtocol.GetLegacyAnnualExchangeRateSnapshot, value, token),
            ReadLegacyAsync,
            ImportLegacyAsync);
        return provider.LoadAsync(year, deadline.Token).GetAwaiter().GetResult();
    }

    private static async Task<CentralParitySnapshot?> ReadSnapshotAsync(string command, int year, CancellationToken token)
    {
        try
        {
            var reply = await Client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), command,
                Payload: year.ToString(CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
            if (reply.Code == "NotAvailable") return null;
            if (reply.Code != "Ok" || reply.Payload is not { } payload)
                throw new IOException("Worker 未能读取年度汇率数据库。");
            return JsonSerializer.Deserialize<CentralParitySnapshot>(payload)
                ?? throw new InvalidDataException("Worker 返回的年度汇率快照无效。");
        }
        catch (BackgroundServiceException error)
        {
            throw new IOException("无法连接 Worker 汇率数据库，请确认后台服务已启动。", error);
        }
    }

    private static async Task<LegacyAnnualExchangeRateRecord[]> ReadLegacyAsync(int year, CancellationToken token)
    {
        try
        {
            var reply = await Client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                WorkerProtocol.GetLegacyAnnualExchangeRates, Payload: year.ToString(CultureInfo.InvariantCulture)), token)
                .ConfigureAwait(false);
            if (reply.Code == "NotAvailable") return [];
            if (reply.Code != "Ok" || reply.Payload is not { } payload)
                throw new IOException("Worker 未能读取历史年度汇率记录。");
            return JsonSerializer.Deserialize<LegacyAnnualExchangeRateRecord[]>(payload) ?? [];
        }
        catch (BackgroundServiceException error)
        {
            throw new IOException("无法连接 Worker 历史汇率数据库，请确认后台服务已启动。", error);
        }
    }

    private static async Task<bool> ImportLegacyAsync(CentralParitySnapshot snapshot, CancellationToken token)
    {
        try
        {
            var reply = await Client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(),
                WorkerProtocol.ImportAnnualExchangeRateSnapshot, Payload: JsonSerializer.Serialize(snapshot)), token)
                .ConfigureAwait(false);
            return reply.Code == "Ok";
        }
        catch (BackgroundServiceException) { return false; }
    }
}
