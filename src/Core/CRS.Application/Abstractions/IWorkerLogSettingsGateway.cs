using CRS.Maintenance;

namespace CRS.Application.Abstractions;

public interface IWorkerLogSettingsGateway
{
    bool CanExecute { get; }
    Task<WorkerLogSettings> LoadLogSettingsAsync(CancellationToken token);
    Task<WorkerLogSettings> SaveLogSettingsAsync(WorkerLogSettings settings, CancellationToken token);
}
