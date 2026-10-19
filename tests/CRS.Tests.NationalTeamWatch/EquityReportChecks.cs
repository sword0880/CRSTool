using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class EquityReportChecks
{
    private static readonly EtfKey Key=new("SSE","600036");
    private static readonly DateOnly From=new(2026,8,1),To=new(2026,10,19);
    internal static byte[] Page(int total=1,int page=1)=>JsonSerializer.SerializeToUtf8Bytes(new{
        SECURITY_CODE=Key.Ticker,START_DATE=From.ToString("yyyy-MM-dd"),END_DATE=To.ToString("yyyy-MM-dd"),TITLE="报告",
        pageHelp=new{total,pageCount=(int)Math.Ceiling(total/10d),pageNo=page,pageSize=10},
        result=Enumerable.Range((page-1)*10,Math.Min(10,Math.Max(0,total-(page-1)*10))).Select(i=>new[]{
            new{SECURITY_CODE=Key.Ticker,ORG_BULLETIN_ID="report"+i,TITLE="合成2026年半年度报告摘要",SSEDATE="2026-08-29",BULLETIN_TYPE_DESC="半年报",URL=$"/disclosure/listedinfo/announcement/c/new/2026-08-29/600036_20260829_S{i}.pdf"},
            new{SECURITY_CODE=Key.Ticker,ORG_BULLETIN_ID="report"+i,TITLE="合成2026年半年度报告",SSEDATE="2026-08-29",BULLETIN_TYPE_DESC="半年报",URL=$"/disclosure/listedinfo/announcement/c/new/2026-08-29/600036_20260829_F{i}.pdf"}
        })});
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS 官方报告："+label);}
    private static void Reject(Action action,string label){try{action();}catch(InvalidDataException){Check(true,label);return;}throw new Exception(label);}
    public static void Run(string root)
    {
        var bytes=Page();var parsed=OfficialEquityReports.Parse(Key,From,To,1,bytes);
        Check(parsed.Total==1&&parsed.Documents.Count==1&&!parsed.Documents[0].Title.Contains("摘要"),"按公告组计分页，保留完整报告而非摘要");
        Check(parsed.Documents[0].Announced==new DateOnly(2026,8,29)&&parsed.Documents[0].State=="Indexed","公告目录日期不是已确认报告截止日");
        Check(OfficialEquityReports.Parse(Key,From,To,1,Page(0)).Documents.Count==0,"官网零页空结果正常，不伪造报告");
        byte[] Change(string old,string value)=>Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(old,value));
        Reject(()=>OfficialEquityReports.Parse(Key,From,To,1,Change("600036","600153")),"其他证券响应拒绝");
        Reject(()=>OfficialEquityReports.Parse(Key,From,To,2,bytes),"错误页码拒绝");
        Reject(()=>OfficialEquityReports.Parse(Key,From,To,1,Change("/disclosure/","https://evil.example/disclosure/")),"外部原文路径拒绝");
        Reject(()=>OfficialEquityReports.Parse(Key,From,To,1,Change("20260829_F0","20260828_F0")),"原文路径日期不匹配拒绝");
        Reject(()=>OfficialEquityReports.Request(new("SZSE","000001"),From,To,1),"未经核验的深市适配不假装成功");
        var rawPath=Path.Combine(root,"report-raw");var raw=new RawEvidenceStore(rawPath);
        using var repository=TestMarketData.Open(Path.Combine(root,"report-data"),raw);
        Reject(()=>repository.SaveEquityReports(Key,From,To,[Page(11)]),"缺少尾页时不提交半份目录");
        Check(repository.EquityReports(Key).Count==0,"分页失败不污染缓存");
        var command=Guid.NewGuid();repository.SaveEquityReports(Key,From,To,[bytes],command);var first=repository.EquityReports(Key).Single();
        repository.SaveEquityReports(Key,From,To,[],command);
        Check(repository.EquityReportCommandComplete(command)&&repository.EquityReports(Key).Single().Id==first.Id,"已提交命令重试不重复采集或写入");
        var next=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Page(11,2)).Replace("report10","report0"));
        Reject(()=>repository.SaveEquityReports(Key,From,To,[Page(11),next]),"跨页重复公告组拒绝");
        var hash=raw.Save(bytes);File.WriteAllText(Path.Combine(rawPath,hash+".raw"),"damaged");
        Check(repository.EquityReports(Key).Single().State=="Critical","目录证据损坏后不能当作有效线索");
        File.WriteAllBytes(Path.Combine(rawPath,hash+".raw"),bytes);
        Check(repository.EquityReports(Key).Single().State=="Indexed","仅恢复到原哈希才解除证据阻断");
        Check(repository.EquityHoldings(null,null,null,false).Count==0&&repository.EquityEvents(Key).Count==0,"找到报告不批准持股或增减结论");
    }
    public static void Real(string path)
    {
        var result=OfficialEquityReports.Parse(new("SSE","600153"),From,To,1,File.ReadAllBytes(path));
        Check(result.Total==1&&result.Documents.Count==1&&result.Documents[0].DocumentUrl.EndsWith("600153_20260829_U7BY.pdf"),"真实官方建发样本字段、分组及完整PDF链接核对通过，仍未核验持仓");
    }
}
