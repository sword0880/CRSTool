using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>固定工行已逐页核对样本；只审核4个匹配账户的A股快照，性质和前期未知，不制造期间交易。</summary>
internal static class IcbcEquityAcceptanceTool
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static async Task Run(string directory,bool review)
    {
        Directory.CreateDirectory(directory);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(8));var token=limit.Token;
        var workspace=new WorkerEtfWorkspace();var pipe=new BackgroundPipeClient();var key=new EtfKey("SSE","601398");
        var disclosures=await workspace.EquityDisclosuresAsync(key,token);
        await File.WriteAllTextAsync(Path.Combine(directory,"disclosures.json"),JsonSerializer.Serialize(disclosures,Json),token);
        if(review)
        {
            var candidates=disclosures.Where(x=>x.State is "NeedsReview" or "Accepted"&&x.SourceDate==new DateOnly(2026,8,29)).ToArray();
            if(candidates.Length!=1)throw new InvalidDataException("工行候选版本不唯一，拒绝自动选取。");var candidate=candidates[0];
            // 预期从PDF第72页（印刷70页）逐行核对；社保H股另有脚注，绝不把两股类合计混入A股。
            var expected=new[]{("汇金公司",124004660940m,34.79m,"汇金投资"),("社保基金会",12331645186m,3.46m,"社保基金"),("中国证券金融股份有限公司",2416131540m,.68m,"证金公司"),("中央汇金资产管理有限责任公司",1013921700m,.28m,"汇金资管")};
            for(var i=0;i<expected.Length;i++)
            {
                var (account,shares,percent,institution)=expected[i];
                if(!candidate.Holders.Any(x=>x.Account==account&&x.Shares==shares&&x.Percent==percent))throw new InvalidDataException("正式候选与逐页核对的股数或比例不符。");
                var input=new EquityReview(candidate.Id,new(2026,6,30),new(2026,8,28),"https://v.icbc.com.cn"+OfficialEquityDocuments.IcbcHalfPath,"PDF P72/印刷70页；A股/股/总普通股本；简称见释义","Codex逐页核对2026-10-25；投资目的未验证",false,"Unknown",Account:account,OriginalConfirmed:true,Institution:institution);
                var payload=JsonSerializer.Serialize(new EtfRequest("equity-review-bound",EquityReview:input));var path=Path.Combine(directory,$"review-{i}.json");BackgroundRequest request;
                if(File.Exists(path))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("审核请求损坏。");
                else
                {
                    request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","equity-review",Payload:payload);
                    await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);await JsonSerializer.SerializeAsync(file,request,Json,token);await file.FlushAsync(token);
                }
                if(request.Payload!=payload||request.JobId!="equity-review")throw new InvalidDataException("已保存审核参数不能被替换。");
                var accepted=await pipe.SendAsync(request,token);await File.WriteAllTextAsync(Path.Combine(directory,$"submitted-{i}.json"),JsonSerializer.Serialize(accepted,Json),token);
                BackgroundResponse status;
                do{await Task.Delay(1000,token);status=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);}while(status.Receipt is {State:not "Completed",Code:not "RetryDeferred"});
                await File.WriteAllTextAsync(Path.Combine(directory,$"receipt-{i}.json"),JsonSerializer.Serialize(status,Json),token);
                if(status.Receipt?.ExecutionId is {} executionId)
                {
                    var execution=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:executionId),token);
                    await File.WriteAllTextAsync(Path.Combine(directory,$"execution-{i}.json"),JsonSerializer.Serialize(execution,Json),token);
                }
                if(status.Receipt is not {State:"Completed",Code:"Succeeded"})throw new InvalidDataException("原审核未确认完成，保留原ID停止后续审核。");
            }
        }
        var rows=(await workspace.EquityHoldingsAsync(null,null,null,false,token)).Where(x=>x.Ticker==key.Ticker).ToArray();
        await File.WriteAllTextAsync(Path.Combine(directory,"holdings.json"),JsonSerializer.Serialize(rows,Json),token);
        if(review&&(rows.Length!=4||rows.Any(x=>x.EvidenceLevel!=EvidenceLevel.DisclosedHolding||x.Nature!="Unknown"||x.Delta.HasValue)))throw new InvalidDataException("正式工行回读未达到4个未知性质的已核快照边界。");
        Console.WriteLine($"READ 工行{rows.Length}条，已核快照{rows.Count(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding)}条；不证明投资目的或期间交易。");
    }
}
