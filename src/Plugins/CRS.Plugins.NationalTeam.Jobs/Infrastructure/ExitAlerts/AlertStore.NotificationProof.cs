using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class AlertStore
{
    private sealed record NotificationInput(AlertFundInput[] Inputs,NamedEtfHolding[] Holdings);
    /// <summary>金融通知包括解除/撤销都冻结本次输入；独立于仅保存触发窗口的历史回算，和事件/Outbox同事务。</summary>
    private void SaveNotificationInput(ExitAlertEvent row,ExitAlertFinding finding)
    {
        if(finding.InputSnapshots is not {Count:>0})throw new InvalidDataException("金融通知缺少本次冻结输入。");
        Execute("INSERT INTO nt_alert_notification_input VALUES($id,$n,$inputs,$holdings)",("$id",row.Id),("$n",row.ChangeNo),
            ("$inputs",JsonSerializer.Serialize(finding.InputSnapshots)),("$holdings",JsonSerializer.Serialize(finding.HoldingSnapshots??[])));
    }
    private NotificationInput[] NotificationInputs(AlertOutboxItem notification)
    {
        if(notification.Kind=="HistoricalCorrection")return HistoricalInputs(notification.EventId).Select(x=>new NotificationInput(x.Inputs,x.Holdings)).ToArray();
        using var command=Command("SELECT payload,holdings FROM nt_alert_notification_input WHERE event_id=$id AND sequence=$n",("$id",notification.EventId),("$n",notification.ChangeNo));
        using var reader=command.ExecuteReader();
        if(!reader.Read())return [];
        return [new(JsonSerializer.Deserialize<AlertFundInput[]>(reader.GetString(0))??throw new InvalidDataException("通知基金输入损坏。"),
            JsonSerializer.Deserialize<NamedEtfHolding[]>(reader.GetString(1))??throw new InvalidDataException("通知持仓输入损坏。"))];
    }
}
