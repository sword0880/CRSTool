using System.Security.Cryptography;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>固定正式候选原件与已逐页核对PDF；隔离复算不是正式服务审核。</summary>
internal static class AbcGoldenChecks
{
    public static void Run(string directory)
    {
        void Check(bool value,string name){if(!value)throw new InvalidDataException(name);Console.WriteLine("PASS 农行真实样本："+name);}
        var source=File.ReadAllBytes(Path.Combine(directory,"abc-original-top10.json"));var pdf=File.ReadAllBytes(Path.Combine(directory,"abc-2026-half.pdf"));
        var uri=new Uri("https://www.abchina.com"+OfficialEquityDocuments.AbcHalfPath);
        Check(Convert.ToHexString(SHA256.HashData(source))=="2C1C4767B8BD45B03787ECD8DAF08756676D9A99F14C4BCED94714AF0ACFF1DA","正式SSE候选原件身份固定");
        Check(Convert.ToHexString(SHA256.HashData(pdf)).Equals(OfficialEquityDocuments.ReviewedIssuerHash(uri),StringComparison.OrdinalIgnoreCase),"232页发行人原件与生产白名单摘要一致");
        Check(OfficialEquityDocuments.Matches(uri,"SSE","601288",new(2026,6,30))&&!OfficialEquityDocuments.Matches(uri,"SSE","601398",new(2026,6,30))&&!OfficialEquityDocuments.Matches(uri,"SSE","601288",new(2025,12,31)),"固定农行原文不能审核其他证券或报告期");
        Check(!OfficialEquityDocuments.IsAllowed(new(uri.AbsoluteUri+"?v=1"))&&!OfficialEquityDocuments.IsAllowed(new(uri.AbsoluteUri.Replace(".pdf",".zip")))&&OfficialEquityDocuments.MaximumEvidenceBytes(OfficialEquityDocuments.AbcHalfHash)==8*1024*1024,"URL参数、ZIP和大文件额度不扩大到农行同目录");
        var expected=new[]{("汇金公司",140488809651m,40.14m,"汇金投资"),("社保基金理事会",23520968297m,6.72m,"社保基金"),("中国证券金融股份有限公司",1842751177m,.53m,"证金公司"),("中央汇金资产管理有限责任公司",1255434700m,.36m,"汇金资管")};
        var parsed=OfficialEquitySource.Parse("601288",source);
        Check(parsed.SourceDate==new DateOnly(2026,8,29)&&parsed.Holders.Count==10&&expected.All(x=>parsed.Holders.Any(h=>h.Account==x.Item1&&h.Shares==x.Item2&&h.Percent==x.Item3)),"生产解析的4个候选账户股数/比例与PDF第74页一致，未混入H股或名义持有人，未扣减受托表决权");
        var root=Path.Combine(Path.GetTempPath(),"CRS.AbcGolden",Guid.NewGuid().ToString("N"));var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using(var repo=TestMarketData.Open(Path.Combine(root,"data"),raw))
        {
            repo.SaveEquityDisclosure("SSE","601288",OfficialEquitySource.Request("SSE","601288"),source);var candidate=repo.EquityDisclosures(new("SSE","601288")).Single();
            var changed=pdf.ToArray();changed[^1]^=1;var rejected=false;
            try{repo.ReviewEquity(new(candidate.Id,new(2026,6,30),new(2026,8,28),uri.AbsoluteUri,"PDF P74","隔离损坏验证",false,"Unknown",Account:expected[0].Item1,OriginalConfirmed:true,Institution:expected[0].Item4),changed);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&repo.EquityHoldings(null,null,null,false).Where(x=>x.Ticker=="601288").All(x=>x.EvidenceLevel!=EvidenceLevel.DisclosedHolding),"同地址原文改变不能继承人工核对，也不留下部分审核");
            foreach(var item in expected)repo.ReviewEquity(new(candidate.Id,new(2026,6,30),new(2026,8,28),uri.AbsoluteUri,"PDF P74/印刷72页，释义P5，A股/股/总普通股本","隔离黄金样本逐页复核",false,"Unknown",Account:item.Item1,OriginalConfirmed:true,Institution:item.Item4),pdf);
            Check(repo.EquityHoldings(null,null,null,false).Where(x=>x.Ticker=="601288").All(x=>x.EvidenceLevel==EvidenceLevel.DisclosedHolding&&x.Nature=="Unknown"&&x.Delta is null),"已核股数不会被强制分类成证券投资或期间交易");
        }
        using var reopened=TestMarketData.Open(Path.Combine(root,"data"),raw);var rows=reopened.EquityHoldings(null,null,null,false).Where(x=>x.Ticker=="601288").ToArray();
        File.WriteAllText(Path.Combine(directory,"golden-isolated-holdings.json"),System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Check(rows.Length==4&&rows.All(x=>x.ReportAsOf==new DateOnly(2026,6,30)&&x.Published==new DateOnly(2026,8,28)&&string.Equals(x.EvidenceHash,OfficialEquityDocuments.ReviewedIssuerHash(uri),StringComparison.OrdinalIgnoreCase)),"独立重启回读保留4个报告期快照及逐账户原件身份");
        var oversizedRejected=false;try{raw.Save(new byte[9*1024*1024]);}catch(InvalidDataException){oversizedRejected=true;}
        Check(oversizedRejected,"任意9MiB原件不能取得已核工行哈希的大文件额度");
        var exported=Path.Combine(root,"export.pdf");raw.ExportVerifiedAsync(Convert.ToHexString(SHA256.HashData(pdf)),exported,[Path.Combine(root,"raw")],default).GetAwaiter().GetResult();
        Check(File.ReadAllBytes(exported).AsSpan().SequenceEqual(pdf),"原件仍经哈希核验及完整字节导出，不裁剪报告迁就上限");
    }
}
