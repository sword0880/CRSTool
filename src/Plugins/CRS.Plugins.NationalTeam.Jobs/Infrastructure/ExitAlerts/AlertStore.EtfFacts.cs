using CRS.Plugins.NationalTeamWatch.Domain;
using DocumentReference=CRS.Plugins.NationalTeamWatch.Infrastructure.EtfEvidenceStore.DocumentReference;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class AlertStore
{
    // 预警只消费同一会话中的ETF证据，不拥有原件、口径或具名持仓表。
    private List<AlertFundMetadata> AlertMetadata()=>etfEvidence.AlertMetadata();
    private List<NamedEtfHolding> NamedEtfHoldings(EtfKey? key=null)=>etfEvidence.NamedEtfHoldings(key);
    private static DocumentReference Reference(AlertFundMetadata row)=>EtfEvidenceStore.Reference(row);
    private static DocumentReference Reference(NamedEtfHolding row)=>EtfEvidenceStore.Reference(row);
    private Dictionary<DocumentReference,AlertDocument?> InspectDocuments(IEnumerable<DocumentReference> rows)=>etfEvidence.InspectDocuments(rows);
    private AlertFundMetadata[] ValidateMetadata(IEnumerable<AlertFundMetadata> rows)=>etfEvidence.ValidateMetadata(rows);
    private NamedEtfHolding[] ValidateHoldings(IEnumerable<NamedEtfHolding> rows)=>etfEvidence.ValidateHoldings(rows);
}
