using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>只核对本次已逐页确认的两个报告期账户；请求先落盘，未知结果回查原ID，不升级投资目的或成交。</summary>
internal static class GongniuEquityAcceptanceTool
{
    public static async Task Run(string directory,bool review)
    {
        Directory.CreateDirectory(directory);var json=new JsonSerializerOptions{WriteIndented=true};
        using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(8));var token=limit.Token;var pipe=new BackgroundPipeClient();var workspace=new WorkerEtfWorkspace();
        var key=new EtfKey("SSE","603195");var disclosures=await workspace.EquityDisclosuresAsync(key,token);
        await File.WriteAllTextAsync(Path.Combine(directory,"disclosures.json"),JsonSerializer.Serialize(disclosures,json),token);
        if(review)
        {
            var candidates=disclosures.Where(x=>x.State is "NeedsReview" or "Accepted"&&x.SourceDate==new DateOnly(2026,8,28)).ToArray();
            if(candidates.Length!=1)throw new InvalidDataException("公牛候选版本不唯一，不能自动选择。");var candidate=candidates[0];
            // P34完整十户与P35人民币普通股一致；表内零增减不当成交，期后注销股本不回填报告期分母。
            var expected=new[]{("全国社保基金一一四组合",7879000m,.44m,"社保基金"),("基本养老保险基金八零八组合",4520552m,.25m,"养老保险基金")};
            for(var i=0;i<expected.Length;i++)
            {
                var(account,shares,percent,institution)=expected[i];
                if(!candidate.Holders.Any(x=>x.Account==account&&x.Shares==shares&&x.Percent==percent))throw new InvalidDataException("候选股数或比例不符，停止后续审核。");
                var input=new EquityReview(candidate.Id,new(2026,6,30),new(2026,8,28),"https://gnstaticcdn.gongniukaiguan.net"+OfficialEquityDocuments.GongniuHalfPath,
                    "PDF P34—35；A股/股/6月30日总股本，非7月注销后股本","Codex逐页核对2026-10-26；投资目的未验证",false,"Unknown",Account:account,OriginalConfirmed:true,Institution:institution);
                var payload=JsonSerializer.Serialize(new EtfRequest("equity-review-bound",EquityReview:input));var path=Path.Combine(directory,$"review-{i}.json");BackgroundRequest request;
                if(File.Exists(path))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("原审核请求损坏。");
                else
                {
                    request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","equity-review",Payload:payload);
                    await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);await JsonSerializer.SerializeAsync(file,request,json,token);await file.FlushAsync(token);
                }
                if(request.Payload!=payload||request.JobId!="equity-review")throw new InvalidDataException("原请求参数不可替换。");
                // 先查实际持久句柄；只有明确UnknownRequest才以同一ID提交，不把观察超时当作重开任务许可。
                var status=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);
                await File.WriteAllTextAsync(Path.Combine(directory,$"observed-before-{i}.json"),JsonSerializer.Serialize(status,json),token);
                if(status.Code=="UnknownRequest")
                {
                    var accepted=await pipe.SendAsync(request,token);
                    await File.WriteAllTextAsync(Path.Combine(directory,$"submitted-{i}.json"),JsonSerializer.Serialize(accepted,json),token);
                    if(accepted.Code!="Accepted")throw new InvalidDataException("未确认受理，保存原请求停止，不重开ID。");
                }
                else if(status.Receipt is null)throw new InvalidDataException("原请求状态不明，停止并保留身份。");
                do
                {
                    await Task.Delay(1000,token);status=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);
                    await File.WriteAllTextAsync(Path.Combine(directory,$"receipt-{i}.json"),JsonSerializer.Serialize(status,json),token);
                    if(status.Receipt is null)throw new InvalidDataException("观察回执缺失，保留原请求停止。");
                }while(status.Receipt is {State:not "Completed",Code:not "RetryDeferred"});
                if(status.Receipt.ExecutionId is {} execution)await File.WriteAllTextAsync(Path.Combine(directory,$"execution-{i}.json"),JsonSerializer.Serialize(await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:execution),token),json),token);
                if(status.Receipt is not {State:"Completed",Code:"Succeeded"})throw new InvalidDataException("本账户未确认完成，保留来源冷却并停止后续账户。");
            }
        }
        var rows=(await workspace.EquityHoldingsAsync(null,null,null,false,token)).Where(x=>x.Ticker==key.Ticker).ToArray();
        await File.WriteAllTextAsync(Path.Combine(directory,"holdings.json"),JsonSerializer.Serialize(rows,json),token);
        if(review&&(rows.Length!=2||rows.Any(x=>x.EvidenceLevel!=EvidenceLevel.DisclosedHolding||x.Nature!="Unknown"||x.Delta is not null)))throw new InvalidDataException("正式回读未达到两个未知性质、差额为空的快照边界。");
        Console.WriteLine($"READ 公牛{rows.Length}条，已核{rows.Count(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding)}条；不证明期间成交。");
    }
}
