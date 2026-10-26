using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>仅供后台巡检协调完整性状态；查询不得调用。</summary>
public interface IEvidenceMaintenanceRepository
{
    void ReconcileEvidenceIntegrity();
}
