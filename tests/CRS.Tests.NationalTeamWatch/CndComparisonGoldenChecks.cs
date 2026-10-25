using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>已逐页核对的两期原件只作隔离比较验收，不把手工摘录伪装成SSE历史响应或正式前期快照。</summary>
internal static class CndComparisonGoldenChecks
{
    public static void Run(string root)
    {
        var passed=0;
        void Check(bool value,string name){if(!value)throw new InvalidDataException(name);passed++;Console.WriteLine("PASS 建发两期原件："+name);}
        string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,file))));
        Check(Hash("cnd-2025-annual.pdf")=="470448A5B8073540B0A6A722E1DDED1558AB9DC06FB44347D0E91D2948B72DF2","年报518页的固定原件身份");
        Check(Hash("cnd-2026-half.pdf")=="6D10EE92FEB04A3EB7B066B89552DD5C3F84D57C016E0F43738AB270A8A166F9","半年报425页的正式已核原件身份");
        var current=JsonSerializer.Deserialize<EquityHoldingRow[]>(File.ReadAllText(Path.Combine(root,"half-holdings.json")))??throw new InvalidDataException("正式只读摘录缺失。");
        Check(current.Length==5&&current.All(x=>x.Ticker=="600153"&&x.ReportAsOf==new DateOnly(2026,6,30)&&x.EvidenceHash?.Equals(Hash("cnd-2026-half.pdf"),StringComparison.OrdinalIgnoreCase)==true&&x.Delta is null),"正式五账户仍无已核前期，不继承隔离算术结果");
        // 年报P158-159与半年报P112-113逐行核对，增减列只用于检验同名账户的原文股数差。
        var prior=new[]{("基本养老保险基金一零零三组合",67250642m,-9728600m),("全国社保基金一零七组合",34616620m,2795700m),("全国社保基金四零三组合",28064763m,1300700m),("中信证券股份有限公司－社保基金1106组合",22789500m,997500m)};
        foreach(var (account,shares,reportedChange) in prior)
        {
            var value=current.Single(x=>x.Account==account).Shares;
            Check(DirectEquityComparison.Compare(shares,value,true,true).Delta==reportedChange,"同名账户股数差与半年报增减列一致："+account);
        }
        var newlyListed=current.Single(x=>x.Account=="全国社保基金六零四组合");
        Check(DirectEquityComparison.Compare(null,newlyListed.Shares,true,false) is {State:"FirstListed",Delta:null},"上期未进前十的六零四组合不能补成零或全部新增");
        Check(newlyListed.Shares==50705713m&&newlyListed.Shares!=42690172m&&50705713m-42690172m==8015541m,"本期原文增量小于期末持股，反证未列示不等于零持股");
        Check(DirectEquityComparison.Compare(67250642m,57522042m,false,true) is {State:"NotComparable",Delta:null},"即使股数能相减，未经可比审核仍不能发布期间差额");
        Console.WriteLine($"PASS 建发两期原件合计 {passed}；非正式前期导入、可比审核或国家队交易证据。");
    }
}
