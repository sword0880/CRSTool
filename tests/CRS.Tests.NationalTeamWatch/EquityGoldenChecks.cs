using System.Security.Cryptography;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
/// <summary>以人工核对的真实原件和独立预期验证生产解析、审核及两期比较。</summary>
internal static class EquityGoldenChecks
{
    public static void Run(string directory)
    {
        void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS 真实公告黄金样本："+label);}
        var json=File.ReadAllBytes(Path.Combine(directory,"600153-official-top10.json"));
        var pdf=File.ReadAllBytes(Path.Combine(directory,"issuer-600153-2026-half.pdf"));
        var previous=File.ReadAllBytes(Path.Combine(directory,"issuer-600153-2025-year.pdf"));
        Check(Convert.ToHexString(SHA256.HashData(json))=="D3EE5B577EE76B6EB511730C91019EBCF866115DA134852FCA04F530AC89BA29","已安装服务原始SSE股东响应哈希固定");
        Check(Convert.ToHexString(SHA256.HashData(pdf))=="6D10EE92FEB04A3EB7B066B89552DD5C3F84D57C016E0F43738AB270A8A166F9"
            &&Convert.ToHexString(SHA256.HashData(previous))=="470448A5B8073540B0A6A722E1DDED1558AB9DC06FB44347D0E91D2948B72DF2","发行人两期正式报告原件哈希固定");
        // 下列预期从已渲染的正式PDF第112页逐行核对；不以接口自身的输出生成预期。
        var expected=new Dictionary<string,(decimal Shares,decimal Percent)>
        {
            ["厦门建发集团有限公司"]=(1356687985,46.79m),
            ["基本养老保险基金一零零三组合"]=(57522042,1.98m),
            ["招商银行股份有限公司－上证红利交易型开放式指数证券投资基金"]=(53774273,1.85m),
            ["全国社保基金六零四组合"]=(50705713,1.75m),
            ["全国社保基金一零七组合"]=(37412320,1.29m),
            ["宁波银行股份有限公司－中泰星元价值优选灵活配置混合型证券投资基金"]=(36264679,1.25m),
            ["招商银行股份有限公司－工银瑞信价值精选混合型证券投资基金"]=(31549196,1.09m),
            ["全国社保基金四零三组合"]=(29365463,1.01m),
            ["中信证券股份有限公司－社保基金1106组合"]=(23787000,.82m),
            ["中国银行股份有限公司－易方达中证红利交易型开放式指数证券投资基金"]=(22162765,.76m)
        };
        var parsed=OfficialEquitySource.Parse("600153",json);
        Check(parsed.SourceDate==new DateOnly(2026,8,29)&&parsed.Holders.Count==10&&parsed.Holders.All(x=>expected.TryGetValue(x.Account,out var value)&&value.Shares==x.Shares&&value.Percent==x.Percent),"真实生产解析器的全部10名账户、数量、比例与正式PDF一致");
        var raw=new RawEvidenceStore(Path.Combine(directory,"golden-isolation",Guid.NewGuid().ToString("N"),"raw"));
        using var repository=TestMarketData.Open(Path.Combine(directory,"golden-isolation",Guid.NewGuid().ToString("N"),"data"),raw);
        repository.SaveEquityDisclosure("SSE","600153",OfficialEquitySource.Request("SSE","600153"),json);
        var candidate=repository.EquityDisclosures(new("SSE","600153")).Single();
        foreach(var account in expected.Keys.Where(x=>InstitutionAccounts.CandidateGroup(x) is not null))
            repository.ReviewEquity(new(candidate.Id,new(2026,6,30),new(2026,8,29),"https://www.chinacnd.com/report-227.html","PDF P112 前十名股东表（单位股、A股、占总股本）","Codex visual sample check; isolated test",true,"Portfolio",Account:account,OriginalConfirmed:true,Institution:account.Contains("1106")?"社保基金":null),pdf);
        var holdings=repository.EquityHoldings(null,null,null,false);
        Check(holdings.Count==5&&holdings.All(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding&&x.Delta is null&&x.State=="FirstListed"),"真实原件经既有逐账户审核路径产生5个有证据的快照，未制造前期交易");
        Check(!holdings.Any(x=>x.Institution.Contains("汇金")),"真实股东表未列汇金时不制造零持仓或清仓事件");
        Check(DirectEquityComparison.Compare(67250642,57522042,true,true)==("Decreased",-9728600m)
            &&DirectEquityComparison.Compare(34616620,37412320,true,true)==("Increased",2795700m)
            &&DirectEquityComparison.Compare(28064763,29365463,true,true)==("Increased",1300700m),"两份正式报告同口径的三个真实期间差额与披露一致，不推断具体交易日期");
        Check(!OfficialEquityDocuments.IsAllowed(new("https://www.chinacnd.com/report-999.html"))&&!OfficialEquityDocuments.Matches(new("https://www.chinacnd.com/report-227.html"),"SSE","600036",new(2026,6,30)),"发行人来源不扩展为任意网页，也不能跨股票套用");
    }
}
