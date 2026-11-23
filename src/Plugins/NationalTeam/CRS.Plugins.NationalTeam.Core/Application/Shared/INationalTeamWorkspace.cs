namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>仅用于会话装配的国家队工作区组合；页面依赖具体功能端口，不重复声明领域方法。</summary>
public interface INationalTeamWorkspace : IEtfResearchWorkspace, IDirectEquityWorkspace,
    IIndustryValuationWorkspace, IMarketCycleWorkspace, INavWorkspace, IExitAlertWorkspace, IEtfHoldingsWorkspace;
