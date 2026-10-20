using CRS.Domain.Performance;

namespace CRS.Application.Performance;

/// <summary>每次任务绑定原解锁会话；取消后不保存半份结果，历史始终另存。</summary>
public sealed partial class PerformanceUseCases(IPerformanceStore store,IPerformanceFiles files,CancellationToken session,IPerformanceMarketData? market=null) : IPerformanceUseCases
{
    private readonly PerformanceOnlineAccess onlineAccess=new();
    public bool OnlineUpdatesAllowed=>!session.IsCancellationRequested && onlineAccess.Allowed;
    public bool SetOnlineUpdatesAllowed(bool userAuthorized)
    {onlineAccess.SetAllowed(userAuthorized && market is not null && !session.IsCancellationRequested);return OnlineUpdatesAllowed;}
    public async Task<bool> FetchBenchmarkAsync(string symbol,PerformanceRequest request,CancellationToken cancellation=default,bool force=false)
    {
        if(market is null)return false;
        using var linked=onlineAccess.Link(session,cancellation);
        var current=await LoadAsync(linked.Token);
        var from=request.SinceInception?PerformanceCalculator.Calculate(current.Data,request,linked.Token).EffectiveFrom:request.From;
        var to=request.To;var currency=request.Currency;
        var prices=current.Data.Prices.Where(p=>p.Security==symbol && p.Market=="INDEX").ToArray();
        var native=PerformanceBenchmarks.Get(symbol).Currency;
        var hasFx=native==currency || current.Data.Prices.Any(p=>p.Market=="INDEX_FX" && p.Security==native+currency && p.Id.StartsWith("Yahoo:FX:"+symbol+":",StringComparison.Ordinal));
        if(!force && hasFx && prices.Any(p=>p.Date<=from && p.Date>=from.AddDays(-7)) && prices.Any(p=>p.Date<=to && p.Date>=to.AddDays(-7)))return false;
        var document=await market.FetchAsync(symbol,from,to,currency,linked.Token);
        return await Run(linked.Token,token=>{token.ThrowIfCancellationRequested();return store.Import(document);});
    }
    public Task<PerformanceCurve> CurveAsync(string runId,string benchmark,CancellationToken cancellation=default)=>Run(cancellation,token=>
    {var run=store.OpenRun(runId);PerformanceEvidence.Verify(run);return PerformanceCurveCalculator.Calculate(run.Input,run.Result,benchmark,token);});
    private Task<T> Run<T>(CancellationToken cancellation,Func<CancellationToken,T> action)=>Task.Run(()=>
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(session,cancellation);
        try {linked.Token.ThrowIfCancellationRequested();var result=action(linked.Token);linked.Token.ThrowIfCancellationRequested();return result;}
        catch(Exception) when(linked.IsCancellationRequested) {throw new OperationCanceledException(linked.Token);}
    });
    public Task<PerformanceWorkspace> LoadAsync(CancellationToken cancellation=default)=>Run(cancellation,_=>store.Load());
    public Task<bool> ImportAsync(string path,string broker,CancellationToken cancellation=default)=>Run(cancellation,token=>
    {var document=files.Read(path,broker,token);token.ThrowIfCancellationRequested();return store.Import(document);});
    public Task<bool> ImportManualAsync(string json,CancellationToken cancellation=default)=>Run(cancellation,token=>
    {var document=files.ReadManual(json);token.ThrowIfCancellationRequested();return store.Import(document);});
    public Task SavePortfolioAsync(InvestmentPortfolio portfolio,CancellationToken cancellation=default)=>Run(cancellation,_=>{store.SavePortfolio(portfolio);return true;});
    public Task RenameAccountAsync(string id,string alias,CancellationToken cancellation=default)=>Run(cancellation,_=>{if(string.IsNullOrWhiteSpace(alias) || alias.Length>100)throw new CrsException("账户别名不能为空或超过 100 字。");store.RenameAccount(id,alias);return true;});
    public Task SetSourceIncludedAsync(string hash,bool included,string reason,CancellationToken cancellation=default)=>Run(cancellation,_=>{if(string.IsNullOrWhiteSpace(reason) || reason.Length>1000)throw new CrsException("来源范围修订必须填写原因。");store.SourceRevision(new(hash,included,reason,DateTimeOffset.UtcNow));return true;});
    public Task ReviseClassificationAsync(string eventId,InvestmentEventKind kind,bool confirmed,string reason,CancellationToken cancellation=default)=>Run(cancellation,_=>
    {if(string.IsNullOrWhiteSpace(reason) || reason.Length>1000 || !Enum.IsDefined(kind))throw new CrsException("分类修订必须填写原因，且类型有效。");store.Revise(new(Guid.NewGuid().ToString("N"),eventId,kind,confirmed,reason,DateTimeOffset.UtcNow));return true;});
    // 基础币种只影响后续补录，历史计算保持原有冻结元数据。
    public Task ConfirmBaseCurrencyAsync(string id,string currency,CancellationToken cancellation=default)=>Run(cancellation,_=>{if(currency.Length!=3 || !currency.All(c=>c is >= 'A' and <= 'Z'))throw new CrsException("请填写三位大写币种。");store.ConfirmBaseCurrency(id,currency);return true;});
    public Task ConfirmTransferAsync(string outgoingId,string incomingId,decimal tolerance,string reason,string toleranceCurrency="CNY",CancellationToken cancellation=default)=>Run(cancellation,_=>
    {if(string.IsNullOrWhiteSpace(reason) || reason.Length>1000 || tolerance is <0 or >100000 || toleranceCurrency.Length!=3 || !toleranceCurrency.All(c=>c is >= 'A' and <= 'Z'))throw new CrsException("请填写转账/证券转仓核对原因及合理差额容差。");store.Transfer(new(Guid.NewGuid().ToString("N"),outgoingId,incomingId,true,tolerance,reason,toleranceCurrency));return true;});
    public Task<PerformanceRun> CalculateAsync(PerformanceRequest request,CancellationToken cancellation=default)
    {
        var frozenRequest=request with{AccountIds=[..request.AccountIds]};
        return Run(cancellation,token=>
        {
            var workspace=store.Load();var data=PerformanceDataSnapshot.Copy(workspace.Data);var result=PerformanceCalculator.Calculate(data,frozenRequest,token);
            var run=PerformanceEvidence.Seal(new PerformanceRun(Guid.NewGuid().ToString("N"),null,DateTimeOffset.UtcNow,files.Hash(data),data,result)
                {ImportProvenance=[..workspace.Sources.Where(s=>s.Included&&s.Provenance is not null).OrderBy(s=>s.Hash,StringComparer.Ordinal).Select(s=>s.Provenance!)]});
            token.ThrowIfCancellationRequested();store.SaveRun(run);return run;
        });
    }
    public Task<PerformanceRun> OpenRunAsync(string id,CancellationToken cancellation=default)=>Run(cancellation,_=>store.OpenRun(id));
    public Task<PerformanceRun> ReplayAsync(string id,CancellationToken cancellation=default)=>Run(cancellation,token=>
    {
        var original=store.OpenRun(id);
        if(original.Result.EngineVersion!=PerformanceCalculator.Version || files.Hash(original.Input)!=original.InputHash)throw new CrsException("历史绩效版本或输入摘要不一致，不能用当前规则冒充原版重放。");
        var result=PerformanceCalculator.Calculate(original.Input,original.Result.Request,token);
        PerformanceEvidence.Verify(original);
        if(System.Text.Json.JsonSerializer.Serialize(result)!=System.Text.Json.JsonSerializer.Serialize(original.Result))throw new CrsException("当前绩效输出与冻结原结果不一致，请明确升级规则并作为新计算运行，不能冒充原版重放。");
        var run=PerformanceEvidence.Seal(new PerformanceRun(Guid.NewGuid().ToString("N"),original.Id,DateTimeOffset.UtcNow,original.InputHash,original.Input,result){ImportProvenance=original.ImportProvenance});
        token.ThrowIfCancellationRequested();store.SaveRun(run);return run;
    });
    public Task ExportAsync(string id,string path,bool encrypted,string password,SensitiveExportConsent? consent=null,CancellationToken cancellation=default)
        =>Run(cancellation,token=>{files.Export(store.OpenRun(id),path,encrypted,password,consent,token);return true;});
}
