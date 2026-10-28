using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>本功能声明自己的公开查询、持久任务及执行组，入口仅负责授权协议和生命周期。</summary>
public sealed partial class NationalTeamBackgroundSession
{
    [NationalTeamQuery("joint-exit-study",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryJointExitStudy(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.To is not {} jointDate||request.KnowledgeCutoff is not null)throw new InvalidDataException("联合观察只提供当前读快照，不支持重建历史知识截止。");
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(await workspace!.Alerts.JointExitStudyAsync(Key(request),jointDate,token),token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-overview",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryAlertOverview(EtfRequest request,CancellationToken token)
    {
        string[] rows;string? metadata=null;
        rows=[JsonSerializer.Serialize(await workspace!.Alerts.AlertOverviewAsync(token))];
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-configuration",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAlertConfiguration(EtfRequest request,CancellationToken token)
    {
        string[] rows;string? metadata=null;
        rows=[JsonSerializer.Serialize(await workspace!.Alerts.AlertConfigurationAsync(token))];
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-events",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAlertEvents(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Alerts.AlertEventsAsync(request.AlertFilter??new(),token)).Select(x=>SerializeRow(x with{RevisionIds=[],HistoricalRevisionIds=null})).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-event",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAlertEvent(EtfRequest request,CancellationToken token)
    {
        string[] rows;string? metadata=null;
        rows=[JsonSerializer.Serialize((await workspace!.Alerts.AlertEventAsync(request.Revision??"",token)) with{RevisionIds=[],HistoricalRevisionIds=null})];
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-event-version",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAlertEventVersion(EtfRequest request,CancellationToken token)
    {
        string[] rows;string? metadata=null;
        if(!int.TryParse(request.Name,out var sequence)||sequence<1)throw new InvalidDataException("历史变更序号无效。");
        rows=[JsonSerializer.Serialize((await workspace!.Alerts.AlertEventVersionAsync(request.Revision??"",sequence,token)) with{RevisionIds=[],HistoricalRevisionIds=null})];
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-inputs",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryAlertInputs(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        int? inputSequence=request.Name is null?null:int.TryParse(request.Name,out var parsedSequence)&&parsedSequence>0?parsedSequence:throw new InvalidDataException("历史输入版本无效。");
        rows=(await workspace!.Alerts.AlertInputsAsync(request.Revision??"",inputSequence,token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-changes",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAlertChanges(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Alerts.AlertChangesAsync(request.Revision??"",token)).Select(x=>SerializeRow(x with{Payload=""})).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-outbox",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAlertOutbox(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(request.AlertFilter is null?await workspace!.Alerts.AlertOutboxAsync(token):await workspace!.Alerts.AlertOutboxAsync(request.AlertFilter,token)).Select(x=>SerializeRow(x with{Body=""})).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-deliveries",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAlertDeliveries(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Alerts.AlertDeliveriesAsync(request.Revision??"",token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-channels",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAlertChannels(EtfRequest request,CancellationToken token)
    {
        string[] rows;string? metadata=null;
        rows=context is IBackgroundNotificationContext notificationContext?(await notificationContext.NotificationChannelsAsync(token)).Select(x=>JsonSerializer.Serialize(new AlertChannel(x.Id,x.Name,x.Enabled,x.Configured))).ToArray():[];
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-documents",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryAlertDocuments(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Alerts.AlertDocumentsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-metadata",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryAlertMetadata(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Alerts.AlertMetadataAsync(token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("alert-holdings",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryAlertHoldings(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Alerts.NamedEtfHoldingsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamJob("alert-evaluate","预警评价与通知提交","0 0/5 * * * ?","China Standard Time",true,true,Group="evaluation",MaximumConcurrency=1,Order=5)]
    private async Task<BackgroundJobResult> ExecuteAlertEvaluate(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation is "sync" or "alert-evaluate"))throw new InvalidDataException("任务与业务参数不匹配。");
        await workspace!.Evidence.ReconcileEvidenceIntegrityAsync(token);
        await workspace!.Alerts.EvaluateAlertsAsync(request.Operation=="alert-evaluate",token);
        return await DispatchAlertNotificationsAsync(token);
    }

    [NationalTeamJob("alert-manage","预警规则与证据管理（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=6)]
    private async Task<BackgroundJobResult> ExecuteAlertManage(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        return await ManageAlertAsync(request,commandId,token);
    }
}
