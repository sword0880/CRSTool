using System.IO;
using System.Windows;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>插件内部独立于宿主的稳定分类；分类是导航概念，不构成金融证据。</summary>
public sealed record NationalTeamWorkspaceCategory(string Key,string Title,string Description);
public sealed record NationalTeamWorkspaceItem(string RouteKey,string CategoryKey,string Title,string EvidenceNotice,Func<FrameworkElement> CreateView);

/// <summary>所有国家队桌面功能从单个目录登记，导航壳不硬编码业务分支或计算金融事实。</summary>
public static class NationalTeamWorkspaceCatalog
{
    public static IReadOnlyList<NationalTeamWorkspaceCategory> Categories {get;}=[
        new("holdings","资金与持仓","ETF份额与机构披露分开，市场份额变动不等于国家队交易"),
        new("industry","行业与估值","官方全行业PE与样本指数PB的证券宇宙不同，禁止跨来源拼接")
    ];

    public static IReadOnlyList<NationalTeamWorkspaceItem> Create(EtfViewModel etfs,EquityViewModel equity,
        IndustryResearchViewModel industry,IndexResearchViewModel indices)
    {
        var result=new NationalTeamWorkspaceItem[]{
            new("etf-monitor","holdings","ETF份额与市场成交","ETF全市场份额不是中央汇金独立持仓。",()=>new EtfPage(etfs)),
            new("direct-equity","holdings","机构直接持股","公告报告期快照不等于逐日真实成交。",()=>new EquityPage(equity)),
            new("industry-pe","industry","全行业公布PE","国证一级行业剔亏PE的短区间历史位置不等于低估。",()=>new IndustryResearchPage(industry)),
            new("sector-index","industry","行业样本指数与PB","国证1000行业样本不能冒充全部上市公司估值。",()=>new IndexResearchPage(indices))
        };
        Validate(result);
        return Array.AsReadOnly(result);
    }

    public static void Validate(IReadOnlyList<NationalTeamWorkspaceItem> items)
    {
        var groups=Categories.Select(x=>x.Key).ToHashSet(StringComparer.Ordinal);
        if(items.Count==0||items.Count>32||Categories.Select(x=>x.Key).Distinct(StringComparer.Ordinal).Count()!=Categories.Count
            ||Categories.Any(x=>string.IsNullOrWhiteSpace(x.Key)||string.IsNullOrWhiteSpace(x.Title)||string.IsNullOrWhiteSpace(x.Description))
            ||items.Select(x=>x.RouteKey).Distinct(StringComparer.Ordinal).Count()!=items.Count
            ||items.Any(x=>string.IsNullOrWhiteSpace(x.RouteKey)||string.IsNullOrWhiteSpace(x.Title)||string.IsNullOrWhiteSpace(x.EvidenceNotice)
                ||!groups.Contains(x.CategoryKey)||x.CreateView is null))
            throw new InvalidDataException("国家队工作台分类、路由或证据说明无效，停止创建导航。");
    }
}
