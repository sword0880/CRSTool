using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
namespace CRS.Plugins.NationalTeamWatch.Domain;

public sealed record InstitutionNameReference(string LegalName,string Group,string? ReferenceUrl=null,string? ReferenceLocation=null);
/// <summary>名称识别目录供ETF及直接持股共用；只匹配具名账户，不证明特定期间持仓、资金用途或经济所有权。</summary>
public static class InstitutionAccounts
{
    public const string Version="institution-names-20261028-v1";
    public const string SafeReference="https://english.eximbank.gov.cn/News/AnnualR/2024eng/202504/P020250429570886425523.pdf";
    public const string ChipFundReference="https://static.cninfo.com.cn/finalpage/2025-12-30/1224907005.PDF";
    public static IReadOnlyList<string> Groups {get;}=Array.AsReadOnly(new[]{"汇金投资","汇金资管","证金公司","社保基金","养老保险基金","外汇局投资平台","国家大基金"});
    public static IReadOnlyList<string> RequiredFamilies {get;}=Array.AsReadOnly(new[]{"中央汇金","证金公司","外汇局投资平台","社保基金","国家大基金"});
    public static IReadOnlyList<InstitutionNameReference> Names {get;}=Array.AsReadOnly(new InstitutionNameReference[]{
        new("中央汇金投资有限责任公司","汇金投资"),new("中央汇金资产管理有限责任公司","汇金资管"),
        new("中国证券金融股份有限公司","证金公司"),new("全国社会保障基金理事会","社保基金"),
        new("梧桐树投资平台有限责任公司","外汇局投资平台",SafeReference,"2024年度报告，梧桐树公司说明"),
        new("国家集成电路产业投资基金股份有限公司","国家大基金",ChipFundReference,"公告2025-038，第六节释义"),
        new("国家集成电路产业投资基金二期股份有限公司","国家大基金",ChipFundReference,"公告2025-038，第六节释义"),
        new("国家集成电路产业投资基金三期股份有限公司","国家大基金",ChipFundReference,"公告2025-038，第六节释义")});
    private static readonly IReadOnlyDictionary<string,string> NamedGroups=new ReadOnlyDictionary<string,string>(Names.ToDictionary(x=>x.LegalName,x=>x.Group,StringComparer.Ordinal));
    public static bool IsSupportedGroup(string? group)=>group is not null&&Groups.Contains(group,StringComparer.Ordinal);
    public static string Family(string group)=>group is "汇金投资" or "汇金资管"?"中央汇金":group;
    public static string? CandidateGroup(string account)=>Group(account)??(account.Trim() is "汇金公司" or "社保基金会"||account.Contains("社保基金",StringComparison.Ordinal)||account.Contains("基本养老保险基金",StringComparison.Ordinal)?"主体待核验":null);
    public static string? Group(string account)
    {
        var name=account.Trim();if(NamedGroups.TryGetValue(name,out var group))return group;
        if(Regex.IsMatch(name,@"^全国社保基金[〇零一二三四五六七八九十百0-9]+组合$",RegexOptions.None,TimeSpan.FromMilliseconds(100)))return "社保基金";
        return Regex.IsMatch(name,@"^基本养老保险基金[〇零一二三四五六七八九十百0-9]+组合$",RegexOptions.None,TimeSpan.FromMilliseconds(100))?"养老保险基金":null;
    }
}
