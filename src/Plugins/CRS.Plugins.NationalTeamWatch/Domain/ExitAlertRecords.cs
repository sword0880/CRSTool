namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>业务订阅只引用渠道和配置Profile；换邮件、钉钉或微信不改变风险算法。</summary>
public sealed record AlertNotificationRoute(string ChannelId="email",string ProfileId="default");
public sealed record ExitAlertConfiguration(ExitAlertRules Rules,IReadOnlyList<AlertNotificationRoute> Routes,long Revision);
public sealed record ExitAlertFilter(string? Lane=null,string? State=null,DateOnly? From=null,DateOnly? To=null,string? Search=null);
public sealed record ExitAlertEvent(string Id,string RuleId,string ScopeId,string RuleVersion,AlertLane Lane,AlertSeverity Severity,
    AlertEvidenceKind EvidenceKind,DateOnly FirstTradeDate,DateOnly LastTradeDate,string State,string DataHealth,bool Preview,
    string FirstSeenAt,string UpdatedAt,string Headline,string Explanation,string Fingerprint,IReadOnlyList<string> RevisionIds,
    int ChangeNo=1,int ClearCount=0,DateOnly? LastClearDate=null,string? HistoricalRevisionNote=null,IReadOnlyList<string>? HistoricalRevisionIds=null)
{
    public string LaneText=>Lane switch{AlertLane.DataQuality=>"数据核查",AlertLane.EtfOutflow=>"ETF份额异常",_=>"机构最低净减少证据"};
    public string SeverityText=>Severity switch{AlertSeverity.Info=>"信息",AlertSeverity.Watch=>"观察",AlertSeverity.Warning=>"警告",_=>"严重"};
    public string StateText=>State switch{"PendingVerification"=>"待核实","Active"=>"持续关注","Resolved"=>"已解除","RetractedByRevision"=>"修订撤销","HistoricalEvidence"=>"历史证明保留","RuleSuperseded"=>"规则版本已替换",_=>State};
    public string DataHealthText=>DataHealth=="Complete"?Lane==AlertLane.DataQuality?"核查依据完整":"本次输入完整":"数据不足，不能据此解除风险";
    public string UsageText=>Lane==AlertLane.DataQuality?"数据核查，不代表金融风险结论":Preview?"研究预览，正式数据门槛未通过":"正式数据门槛通过";
    public string EvidenceText=>EvidenceKind switch{AlertEvidenceKind.OfficialMarketObserved=>"公开市场份额",AlertEvidenceKind.PendingQualityReview=>"数据待核查",
        AlertEvidenceKind.NamedHoldingVerified=>"具名披露已核验",AlertEvidenceKind.DerivedMinimumReduction=>"最低净减少下界",AlertEvidenceKind.ResearchHypothesis=>"研究假设",_=>"证据不足"};
}
public sealed record ExitAlertChange(string EventId,int Sequence,string Kind,string At,string Payload)
{
    public string KindText=>Kind switch{"FirstObserved"=>"首次发现","DataUnavailable"=>"数据不足","UpdatedEvidence"=>"证据更正或风险升级","ObservationUpdated"=>"观察更新",
        "RetractedByRevision"=>"修订撤销","Resolved"=>"解除观察","ClearPending"=>"等待连续解除观察","RuleSuperseded"=>"规则版本替换","HistoricalCorrection"=>"历史输入更正",_=>Kind};
}
public sealed record AlertOutboxItem(string Id,string EventId,int ChangeNo,string ChannelId,string ProfileId,string State,int Attempts,
    string NextAttemptAt,string MessageId,string Subject,string Body,string CreatedAt,string? LastCode=null,string? AcceptedAt=null,string? Lease=null,int Failures=0,string? Kind=null)
{
    public string StatusText=>State switch{"Pending"=>"待发送","InFlight"=>"正在提交","Accepted"=>"渠道已受理，送达未验证","Retry"=>"等待重试",
        "Disabled"=>"渠道未启用","Unavailable"=>"渠道不可用","Failed"=>"发送失败","OutcomeUnknown"=>"受理结果未知，需核对",
        "BlockedAcceptance"=>"正式数据门槛未通过，禁止外发","Superseded"=>"已被更新替代",_=>State};
}
public sealed record AlertDeliveryRecord(string Id,string OutboxId,int Attempt,string At,string State,string Code,string? ProviderReceipt);
/// <summary>业务只接触渠道能力和提交结果，不依赖邮件或具体提供商程序集。</summary>
public sealed record AlertChannel(string Id,string Name,bool Enabled,bool Configured);
public sealed record AlertDispatchOutcome(string State,string Code,string? ProviderReceipt=null);
public sealed record ExitAlertOverview(int MonitoredFunds,int ComparableFunds,DateOnly? LatestTradeDate,bool DataAcceptancePassed,
    int PendingFacts,int ActiveEvents,string RuleVersion,string MarketRiskState="缺少可验证的承接行情，不生成Critical",string Availability="ResearchPreview");
/// <summary>先登记原件再人工审核；同URL更换字节不能继承旧确认。</summary>
public sealed record AlertDocument(string Id,string Market,string Ticker,string Url,string Hash,string FirstSeenAt);
public sealed record AlertDocumentRequest(string Market,string Ticker,string Url);
public sealed record AlertMetadataReview(string DocumentId,string FundId,string ManagerId,string IndexId,DateOnly ComparableFrom,
    DateOnly ComparableThrough,string Reviewer,bool OriginalConfirmed);
public sealed record NamedEtfHoldingReview(string DocumentId,string LegalEntityId,string LegalName,string Account,DateOnly HoldingAsOf,
    DateTimeOffset PublishedAt,decimal Shares,string EvidencePage,string Reviewer,bool OriginalConfirmed,bool IndependentOwnershipConfirmed);
