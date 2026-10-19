namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>业务订阅只引用渠道和配置Profile；换邮件、钉钉或微信不改变风险算法。</summary>
public sealed record AlertNotificationRoute(string ChannelId="email",string ProfileId="default");
public sealed record ExitAlertConfiguration(ExitAlertRules Rules,IReadOnlyList<AlertNotificationRoute> Routes,long Revision);
public sealed record ExitAlertFilter(string? Lane=null,string? State=null,DateOnly? From=null,DateOnly? To=null,string? Search=null);
public sealed record ExitAlertEvent(string Id,string RuleId,string ScopeId,string RuleVersion,AlertLane Lane,AlertSeverity Severity,
    AlertEvidenceKind EvidenceKind,DateOnly FirstTradeDate,DateOnly LastTradeDate,string State,string DataHealth,bool Preview,
    string FirstSeenAt,string UpdatedAt,string Headline,string Explanation,string Fingerprint,IReadOnlyList<string> RevisionIds,
    int ChangeNo=1,int ClearCount=0,DateOnly? LastClearDate=null)
{
    public string LaneText=>Lane switch{AlertLane.DataQuality=>"数据核查",AlertLane.EtfOutflow=>"ETF份额异常",_=>"机构最低净减少证据"};
    public string SeverityText=>Severity switch{AlertSeverity.Info=>"信息",AlertSeverity.Watch=>"观察",AlertSeverity.Warning=>"警告",_=>"严重"};
    public string StateText=>State switch{"PendingVerification"=>"待核实","Active"=>"持续关注","Resolved"=>"已解除","RetractedByRevision"=>"修订撤销",_=>State};
    public string DataHealthText=>DataHealth=="Complete"?"本次输入完整":"数据不足，不能据此解除风险";
    public string UsageText=>Preview?"研究预览，正式数据门槛未通过":"正式数据门槛通过";
}
public sealed record ExitAlertChange(string EventId,int Sequence,string Kind,string At,string Payload);
public sealed record AlertOutboxItem(string Id,string EventId,int ChangeNo,string ChannelId,string ProfileId,string State,int Attempts,
    string NextAttemptAt,string MessageId,string Subject,string Body,string CreatedAt,string? LastCode=null,string? AcceptedAt=null,string? Lease=null)
{
    public string StatusText=>State switch{"Pending"=>"待发送","InFlight"=>"正在提交","Accepted"=>"渠道已受理，送达未验证","Retry"=>"等待重试",
        "Disabled"=>"渠道未启用","Unavailable"=>"渠道不可用","Failed"=>"发送失败","OutcomeUnknown"=>"受理结果未知，需核对",
        "BlockedAcceptance"=>"正式数据门槛未通过，禁止外发","Superseded"=>"已被更新替代",_=>State};
}
public sealed record AlertDeliveryRecord(string Id,string OutboxId,int Attempt,string At,string State,string Code,string? ProviderReceipt);
public sealed record ExitAlertOverview(int MonitoredFunds,int ComparableFunds,DateOnly? LatestTradeDate,bool DataAcceptancePassed,
    int PendingFacts,int ActiveEvents,string RuleVersion,string MarketRiskState="缺少可验证的承接行情，不生成Critical",string Availability="ResearchPreview");
/// <summary>先登记原件再人工审核；同URL更换字节不能继承旧确认。</summary>
public sealed record AlertDocument(string Id,string Market,string Ticker,string Url,string Hash,string FirstSeenAt);
public sealed record AlertDocumentRequest(string Market,string Ticker,string Url);
public sealed record AlertMetadataReview(string DocumentId,string FundId,string ManagerId,string IndexId,DateOnly ComparableFrom,
    DateOnly ComparableThrough,string Reviewer,bool OriginalConfirmed);
public sealed record NamedEtfHoldingReview(string DocumentId,string LegalEntityId,string LegalName,string Account,DateOnly HoldingAsOf,
    DateTimeOffset PublishedAt,decimal Shares,string EvidencePage,string Reviewer,bool OriginalConfirmed,bool IndependentOwnershipConfirmed);
