using System.Text.RegularExpressions;
using CRS.Domain.Performance;

namespace CRS.Application.Performance;

public sealed record PerformanceAccountIdentity(string AccountId,string AccountName,string AccountNumber);

/// <summary>只产生展示投影；原始账户、计算输入和历史摘要保持原值。</summary>
public sealed class PerformanceAccountPrivacy(IEnumerable<InvestmentAccount> accounts,IEnumerable<PerformanceAccountIdentity> identities)
{
    private readonly Dictionary<string,PerformanceAccountIdentity> identities=identities.GroupBy(i=>i.AccountId).ToDictionary(g=>g.Key,g=>g.First());
    private readonly InvestmentAccount[] accounts=accounts.ToArray();
    private readonly Dictionary<string,InvestmentAccount> aliases=accounts.GroupBy(a=>a.Alias).ToDictionary(g=>g.Key,g=>g.First());
    private readonly string aliasPattern=string.Join("|",accounts.Select(a=>a.Alias).Where(a=>a.Length>0).Distinct().OrderByDescending(a=>a.Length).Select(Regex.Escape));
    public string Account(InvestmentAccount account)
    {
        identities.TryGetValue(account.Id,out var identity);
        var alias=account.Alias;
        if(identity is not null && alias.StartsWith("富途 ",StringComparison.Ordinal) && alias.EndsWith($"（{identity.AccountNumber}）",StringComparison.Ordinal))alias="富途账户";
        if(account.Id.Length>=6 && alias=="IBKR 账户 "+account.Id[^6..])alias="IBKR账户";
        if(identity is null)return Mask(alias);
        alias=Mask(alias.Replace(identity.AccountNumber,"",StringComparison.Ordinal)).Trim(' ','（','）','(',')','·');
        if(alias.Length==0)alias=account.Broker+"账户";
        return alias+" · 尾号 "+LastFour(identity.AccountNumber);
    }
    public string Text(string text)
    {
        if(aliases.TryGetValue(text,out var account))return Account(account);
        if(aliasPattern.Length>0)text=Regex.Replace(text,aliasPattern,m=>Account(accounts.First(a=>a.Alias==m.Value)),RegexOptions.CultureInvariant);
        foreach(var identity in identities.Values.OrderByDescending(i=>i.AccountNumber.Length))
            if(identity.AccountNumber.Length>0)
            {
                text=Regex.Replace(text,@"富途 [^（\r\n]*（"+Regex.Escape(identity.AccountNumber)+"）",_=>"富途账户 · 尾号 "+LastFour(identity.AccountNumber),RegexOptions.CultureInvariant);
                text=text.Replace(identity.AccountNumber,"****"+LastFour(identity.AccountNumber),StringComparison.Ordinal);
            }
        return Mask(text);
    }
    public string Details(InvestmentAccount account)
    {
        var identity=identities.GetValueOrDefault(account.Id);
        return $"完整别名：{account.Alias}\n开户名称：{identity?.AccountName??"原件未提供"}\n账户号码：{identity?.AccountNumber??"原件未提供，不以内部摘要代替"}";
    }
    private static string LastFour(string value)=>value.Length<=4?value:value[^4..];
    private static string Mask(string value)=>Regex.Replace(value,@"\d{5,}",m=>"****"+m.Value[^4..],RegexOptions.CultureInvariant);
}
