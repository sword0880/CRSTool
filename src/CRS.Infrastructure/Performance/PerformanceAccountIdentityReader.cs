using CRS.Application.Performance;
using static CRS.Infrastructure.XmlFields;
using static CRS.Infrastructure.PerformanceBrokerMapping;

namespace CRS.Infrastructure;

/// <summary>从加密保存的原件读取账号，仅供遮蔽展示及用户主动查看，不写回历史。</summary>
internal static class PerformanceAccountIdentityReader
{
    internal static IEnumerable<PerformanceAccountIdentity> Read(FrozenInput input,string format)
    {
        if(format==".xlsx")
        {
            using var table=new FutuTable(input);
            return table.Read("账户信息",true,"年份","账户号码","账户名称")!
                .Where(r=>r["账户号码"]!="").Select(r=>new PerformanceAccountIdentity(AccountId("FUTU","FUTU:"+r["账户号码"]),r["账户名称"],r["账户号码"])).ToArray();
        }
        if(format!=".xml")return [];
        var facts=IbkrSourceReader.Read(input,default);
        return facts.Root.Section("FlexStatements")!.Rows("FlexStatement").Select(s=>
        {
            var number=Required(s,"accountId");var info=s.Section("AccountInformation");
            return new PerformanceAccountIdentity(AccountId("IBKR",number),info is null?"原件未提供":Optional(info,"name",Optional(info,"accountAlias","原件未提供")),number);
        }).ToArray();
    }
}
