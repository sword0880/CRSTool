using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;
/// <summary>正式服务验收入口；真实读写均须显式参数，输出独立原始验收记录。</summary>
internal static class P0LiveOperations
{
    private static string DirectoryPath=>Path.Combine(Environment.CurrentDirectory,"artifacts","p0-live");
    /// <summary>只提交现有官方采集任务，后续以持久回执和真实数据回读判定进展。</summary>
    public static async Task Collect()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var pipe=new BackgroundPipeClient();
        Directory.CreateDirectory(DirectoryPath);var receipts=new List<object>();
        foreach(var job in new[]{"official-calendar-sync","official-share-sync"})
        {
            var id=Guid.NewGuid();
            var response=await pipe.SendAsync(new(WorkerProtocol.Version,id,"RunNow","crs.national-team",job,Payload:JsonSerializer.Serialize(new EtfRequest("sync"))),deadline.Token);
            receipts.Add(new{Job=job,Id=id,response.Code,response.Receipt});Console.WriteLine($"QUEUE {job}: {response.Code}");
        }
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath,"collection-command-receipts.json"),JsonSerializer.Serialize(receipts,new JsonSerializerOptions{WriteIndented=true}),deadline.Token);
    }
    /// <summary>按证券读取真实异常修订与邻日事实，不执行审核或自动确认可比性。</summary>
    public static async Task EvidenceCase()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var reader=new WorkerEtfWorkspace();var key=new EtfKey("SSE","510310");
        var evidence=await reader.EvidenceAsync(key,deadline.Token);var points=await reader.PointsAsync(key,deadline.Token);
        Directory.CreateDirectory(DirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath,"installed-510310-evidence.json"),JsonSerializer.Serialize(new{Evidence=evidence,Points=points},new JsonSerializerOptions{WriteIndented=true}),deadline.Token);
        foreach(var row in evidence.Where(x=>x.State=="NeedsReview"))Console.WriteLine($"READ 待审 {row.TradeDate} {row.Shares} {row.OriginalHash}");
    }
    /// <summary>读取真实逐证券门槛，不能将回填数量或合成测试替代连续采集天数。</summary>
    public static async Task Readiness()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var reader=new WorkerEtfWorkspace();
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        var rows=await reader.AcceptanceAsync(today.AddDays(-1),deadline.Token);
        var result=new{CheckedAt=DateTimeOffset.UtcNow,Use="PersonalLocalNonCommercialNoRedistribution",HumanGoldenConfirmed="UserConfirmed2026-10-19",DataGatePassed=rows.Count>0&&rows.All(x=>x.DataAcceptancePassed),Rows=rows};
        Directory.CreateDirectory(DirectoryPath);await File.WriteAllTextAsync(Path.Combine(DirectoryPath,"installed-readiness.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}),deadline.Token);
        foreach(var row in rows)Console.WriteLine($"READ {row.Market}:{row.Ticker}: history={row.AcceptedDays}/{row.ExpectedDays};continuous={row.ContinuousCollectionDays}/20;calendar={row.CalendarVerified};passed={row.DataAcceptancePassed}");
    }
    /// <summary>仅显式命令执行；样本由用户2026-10-19逐项确认，先重验原件与候选哈希，再提交持久命令。</summary>
    public static async Task ReviewConfirmedSample()
    {
        EquityGoldenChecks.Run(DirectoryPath);
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=deadline.Token;
        var reader=new WorkerEtfWorkspace();var pipe=new BackgroundPipeClient();
        var candidate=(await reader.EquityDisclosuresAsync(new("SSE","600153"),token)).Single(x=>x.Hash=="D3EE5B577EE76B6EB511730C91019EBCF866115DA134852FCA04F530AC89BA29");
        var accounts=candidate.Holders.Where(x=>InstitutionAccounts.CandidateGroup(x.Account) is not null).Select(x=>x.Account).ToArray();
        if(accounts.Length!=5)throw new InvalidDataException("已确认样本账户发生变化，停止审核。");
        var path=Path.Combine(DirectoryPath,"equity-review-command-ids.json");
        var ids=File.Exists(path)?JsonSerializer.Deserialize<Dictionary<string,Guid>>(await File.ReadAllTextAsync(path,token))!:new();
        foreach(var account in accounts)if(!ids.ContainsKey(account))ids.Add(account,Guid.NewGuid());
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(ids,new JsonSerializerOptions{WriteIndented=true}),token);
        var receipts=new List<object>();
        foreach(var account in accounts)
        {
            var review=new EquityReview(candidate.Id,new(2026,6,30),new(2026,8,29),"https://www.chinacnd.com/report-227.html","PDF P112 前十名股东表；单位股/A股/总股本口径","用户逐项确认2026-10-19；Codex代提交",true,"Portfolio",Account:account,OriginalConfirmed:true,Institution:InstitutionAccounts.Group(account)??"社保基金");
            var response=await pipe.SendAsync(new(WorkerProtocol.Version,ids[account],"RunNow","crs.national-team","equity-review",Payload:JsonSerializer.Serialize(new EtfRequest("equity-review",EquityReview:review))),token);
            receipts.Add(new{Account=account,Id=ids[account],response.Code,response.Receipt});
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath,"equity-review-receipts.json"),JsonSerializer.Serialize(receipts,new JsonSerializerOptions{WriteIndented=true}),token);
            if(response.Code is not ("Accepted" or "Succeeded" or "RetryDeferred")&&response.Receipt is null)throw new InvalidDataException("服务未接受审核："+response.Code);
        }
        foreach(var account in accounts)
        {
            while(true)
            {
                var response=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:ids[account]),token);
                if(response.Receipt?.State=="Completed")
                {
                    if(response.Receipt.Code!="Succeeded")throw new InvalidDataException("审核未成功："+response.Receipt.Code);
                    break;
                }
                await Task.Delay(1000,token);
            }
        }
        var rows=(await reader.EquityHoldingsAsync("600153",null,null,false,token)).Where(x=>x.Ticker=="600153").ToArray();
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath,"installed-reviewed-600153.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}),token);
        if(rows.Length!=5||rows.Any(x=>x.EvidenceLevel!=EvidenceLevel.DisclosedHolding||x.Delta.HasValue||x.State!="FirstListed"))throw new InvalidDataException("正式快照与已确认样本不一致。");
        Console.WriteLine("PASS 正式服务5个已确认账户全部完成审核；保留原文、逐账户位置及首次列示语义，无虚构交易。");
    }
}
