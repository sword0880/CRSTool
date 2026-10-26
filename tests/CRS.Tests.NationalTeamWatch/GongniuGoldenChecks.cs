using System.Security.Cryptography;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>真实SSE候选与发行人PDF逐页复核；隔离库验证不能代替正式Worker审核回执。</summary>
internal static class GongniuGoldenChecks
{
    public static void Run(string directory)
    {
        void Check(bool value,string name){if(!value)throw new InvalidDataException(name);Console.WriteLine("PASS 公牛真实样本："+name);}
        var source=File.ReadAllBytes(Path.Combine(directory,"gongniu-original-top10.json"));var pdf=File.ReadAllBytes(Path.Combine(directory,"gongniu-2026-half.pdf"));
        var uri=new Uri("https://gnstaticcdn.gongniukaiguan.net"+OfficialEquityDocuments.GongniuHalfPath);
        Check(Convert.ToHexString(SHA256.HashData(source))=="9509AEA937F8D1C3359AB95F27E8995A209488940EECFDFC2D7871244D9E2FD4","正式候选原件身份一致");
        Check(Convert.ToHexString(SHA256.HashData(pdf)).Equals(OfficialEquityDocuments.ReviewedIssuerHash(uri),StringComparison.OrdinalIgnoreCase),"162页半年报固定摘要与逐页样本一致");
        Check(OfficialEquityDocuments.IsAllowed(uri)&&OfficialEquityDocuments.Matches(uri,"SSE","603195",new(2026,6,30))
            &&!OfficialEquityDocuments.Matches(uri,"SZSE","603195",new(2026,6,30))&&!OfficialEquityDocuments.Matches(uri,"SSE","600153",new(2026,6,30))
            &&!OfficialEquityDocuments.Matches(uri,"SSE","603195",new(2025,12,31)),"固定证券/市场/报告期，不能跨期继承审核");
        Check(!OfficialEquityDocuments.IsAllowed(new(uri.AbsoluteUri+"?v=1"))&&!OfficialEquityDocuments.IsAllowed(new(uri.AbsoluteUri+"#p34"))
            &&!OfficialEquityDocuments.IsAllowed(new(uri.AbsoluteUri.Replace(".pdf",".zip")))&&!OfficialEquityDocuments.IsAllowed(new("https://gnstaticcdn.gongniukaiguan.net/other.pdf"))
            &&!OfficialEquityDocuments.IsAllowed(new("https://www.gongniu.cn/investor/index.html")),"不扩大CDN目录、参数、ZIP或普通网页权限");
        var expected=new[]{("全国社保基金一一四组合",7879000m,.44m,"社保基金"),("基本养老保险基金八零八组合",4520552m,.25m,"养老保险基金")};
        var parsed=OfficialEquitySource.Parse("603195",source);
        Check(parsed.SourceDate==new DateOnly(2026,8,28)&&parsed.Holders.Count==10&&expected.All(x=>parsed.Holders.Any(h=>h.Account==x.Item1&&h.Shares==x.Item2&&h.Percent==x.Item3)),"完整十户内两个具名账户与P34—35股数/比例一致，名义持有人未归入国家队");
        var root=Path.Combine(Path.GetTempPath(),"CRS.GongniuGolden",Guid.NewGuid().ToString("N"));var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using(var repo=TestMarketData.Open(Path.Combine(root,"data"),raw))
        {
            repo.SaveEquityDisclosure("SSE","603195",OfficialEquitySource.Request("SSE","603195"),source);
            var candidate=repo.EquityDisclosures(new("SSE","603195")).Single();
            EquityReview Review((string Account,decimal Shares,decimal Percent,string Group) item)=>new(candidate.Id,new(2026,6,30),new(2026,8,28),uri.AbsoluteUri,
                "PDF P34—35；A股/股/6月30日总股本，非7月注销后股本","Codex逐页核对2026-10-26；隔离复算，投资目的未知",false,"Unknown",Account:item.Account,OriginalConfirmed:true,Institution:item.Group);
            // 同一地址的字节变化必须先拒绝，不得留下已核金融事实或把后续重试当成既有成功。
            var changed=pdf.ToArray();changed[^1]^=1;var rejected=false;
            try{repo.ReviewEquity(Review(expected[0]),changed);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&repo.EquityHoldings(null,null,null,false).Where(x=>x.Ticker=="603195").All(x=>x.EvidenceLevel!=EvidenceLevel.DisclosedHolding),"原文损坏失败关闭且无部分审核");
            foreach(var item in expected)repo.ReviewEquity(Review(item),pdf);
        }
        using var reopened=TestMarketData.Open(Path.Combine(root,"data"),raw);var rows=reopened.EquityHoldings(null,null,null,false).Where(x=>x.Ticker=="603195").ToArray();
        Check(rows.Length==2&&rows.All(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding&&x.ReportAsOf==new DateOnly(2026,6,30)&&x.Published==new DateOnly(2026,8,28)
            &&x.Nature=="Unknown"&&x.Delta is null&&string.Equals(x.EvidenceHash,OfficialEquityDocuments.GongniuHalfHash,StringComparison.OrdinalIgnoreCase)),"重启回读两个已核快照，投资性质及期间变化仍未知");
        var exported=Path.Combine(root,"export.pdf");raw.ExportVerifiedAsync(OfficialEquityDocuments.GongniuHalfHash.ToUpperInvariant(),exported,[Path.Combine(root,"raw")],default).GetAwaiter().GetResult();
        Check(File.ReadAllBytes(exported).AsSpan().SequenceEqual(pdf),"完整原文可核验导出，不裁剪报告或回填知识时点");
    }
}
