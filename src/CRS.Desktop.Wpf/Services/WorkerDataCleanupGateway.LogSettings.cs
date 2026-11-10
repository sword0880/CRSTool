using System.Text.Json;
using CRS.Maintenance;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.Services;

public sealed partial class WorkerDataCleanupGateway
{
    public Task<WorkerLogSettings> LoadLogSettingsAsync(CancellationToken token) =>
        RequestLogSettingsAsync(WorkerProtocol.GetWorkerLogSettings, null, token);

    public Task<WorkerLogSettings> SaveLogSettingsAsync(WorkerLogSettings settings, CancellationToken token) =>
        RequestLogSettingsAsync(WorkerProtocol.SaveWorkerLogSettings, JsonSerializer.Serialize(settings), token);

    private async Task<WorkerLogSettings> RequestLogSettingsAsync(string command, string? payload, CancellationToken token)
    {
        BackgroundResponse reply;
        try { reply = await send(new(WorkerProtocol.Version, Guid.NewGuid(), command, Payload: payload), token).ConfigureAwait(false); }
        catch (BackgroundServiceException error) when (error.OutcomeUnknown)
        { throw new InvalidOperationException("后台未确认保存结果，请刷新保留天数后再试。", error); }
        if (reply is not { Code: "Ok", Payload: { } json }) throw new InvalidOperationException(reply.Code switch
        {
            "Forbidden" => "请以管理员身份运行 CRS 后修改后台日志设置。",
            "UnknownCommand" or "UnsupportedCommand" => "当前后台版本不支持设置日志保留天数。",
            "InvalidLogSettings" => "请填写 1 至 365 的整数天数。",
            "LogSettingsConflict" => "其他窗口已修改设置，请刷新后重试。",
            "Busy" => "后台正在清理资料，请在清理结束后保存。",
            _ => "后台日志设置暂不可用。"
        });
        try
        {
            var settings = JsonSerializer.Deserialize<WorkerLogSettings>(json);
            if (settings is { Revision: >= 0 } && WorkerLogSettings.IsValidRetention(settings.RetentionDays)) return settings;
        }
        catch (JsonException) { }
        throw new InvalidOperationException("后台返回的日志设置无效。");
    }
}
