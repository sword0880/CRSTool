using System.Text.Json;
using CRS.Maintenance;
using CRS.Serialization;
using CRS.Worker.Ipc;

namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private async Task<BackgroundResponse> HandleLogSettingsAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
    {
        BackgroundResponse Reply(string code, WorkerLogSettings? value = null) => new(WorkerProtocol.Version, request.RequestId, code,
            Payload: value is null ? null : JsonSerializer.Serialize(value));
        if (!caller.CanManage) return Reply("Forbidden");
        if (cleanup?.CanConfigureLogs != true) return Reply("UnsupportedCommand");
        var save = request.Command == WorkerProtocol.SaveWorkerLogSettings;
        if (request.RequestId == Guid.Empty || request != new BackgroundRequest(WorkerProtocol.Version, request.RequestId,
            request.Command, Payload: request.Payload) || save != (request.Payload is not null)) return Reply("InvalidRequest");

        await gate.WaitAsync(token);
        try
        {
            if (maintenance || fault is not null) return Reply("Maintenance");
            if (!save) return Reply("Ok", cleanup.ReadLogSettings());
            WorkerLogSettings update;
            try { update = StrictJson.Read<WorkerLogSettings>(request.Payload!, 512); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { return Reply("InvalidLogSettings"); }
            if (!WorkerLogSettings.IsValidRetention(update.RetentionDays) || update.Revision < 0) return Reply("InvalidLogSettings");
            token.ThrowIfCancellationRequested();
            var result = cleanup.SaveLogSettings(update);
            if (result is null) return Reply("LogSettingsConflict");
            diagnostics?.Invoke("WorkerLogSettings", "service", "Saved");
            return Reply("Ok", result);
        }
        catch (InvalidOperationException) { return Reply("Busy"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return Reply("LogSettingsUnavailable"); }
        finally { gate.Release(); }
    }
}
