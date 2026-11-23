using System.Windows;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;
using CRS.DesktopClient.Views.Pages.AI;
using Wpf.Ui.Abstractions;
namespace CRS.DesktopClient.Services;
/// <summary>为导航提供有状态页面缓存，不构造业务基础设施。</summary>
internal sealed class PageProvider(ShellViewModel shell) : INavigationViewPageProvider
{
    private readonly Dictionary<Type, object> pages = [];
    // 截图只使用已导航且实际显示的页面，不为截图构建页面或访问隐藏数据。
    public FrameworkElement? FindCreatedPage(Type pageType) => pages.GetValueOrDefault(pageType) as FrameworkElement;
    public object? GetPage(Type pageType)
    {
        if (pages.TryGetValue(pageType, out var page)) return page;
        page = pageType == typeof(DashboardPage) ? new DashboardPage(shell)
            : pageType == typeof(ImportPage) ? new ImportPage(shell)
            : pageType == typeof(TradesPage) ? new TradesPage(shell)
            : pageType == typeof(FifoPage) ? new FifoPage(shell)
            : pageType == typeof(ExchangeRatesPage) ? new ExchangeRatesPage(shell)
            : pageType == typeof(TaxCalculationPage) ? new TaxCalculationPage(shell)
            : pageType == typeof(ReconciliationPage) ? new ReconciliationPage(shell)
            : pageType == typeof(ReportsPage) ? new ReportsPage(shell)
            : pageType == typeof(HistoryPage) ? new HistoryPage(shell)
            : pageType == typeof(PerformancePage) ? new PerformancePage(shell)
            : pageType == typeof(AiChatHistoryPage) ? new AiChatHistoryPage(shell) : null;
        if (page is not null) pages[pageType] = page;
        return page;
    }
}
