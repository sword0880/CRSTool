using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>每个功能按需构造，工作台只保留一个活跃视图；数据和任务由各自ViewModel/Worker负责。</summary>
public sealed class NationalTeamWorkspacePage:UserControl
{
    private readonly IReadOnlyDictionary<string,NationalTeamWorkspaceItem> items;
    private readonly ContentControl content=new();
    private readonly TextBlock title=new(){FontSize=21,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock notice=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,10)};
    private readonly Dictionary<string,Button> buttons=new(StringComparer.Ordinal);
    public string? SelectedRouteKey {get;private set;}
    public int CategoryCount {get;}

    public NationalTeamWorkspacePage(IReadOnlyList<NationalTeamWorkspaceItem> entries)
    {
        NationalTeamWorkspaceCatalog.Validate(entries);
        items=entries.ToDictionary(x=>x.RouteKey,StringComparer.Ordinal);
        CategoryCount=NationalTeamWorkspaceCatalog.Categories.Count(c=>entries.Any(e=>e.CategoryKey==c.Key));
        var root=new Grid{Margin=new Thickness(16)};
        root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(248)});
        root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});

        var menu=new StackPanel{Margin=new Thickness(0,0,10,0)};
        menu.Children.Add(new TextBlock{Text="国家队研究工作台",FontSize=21,FontWeight=FontWeights.SemiBold,Margin=new Thickness(8,0,0,8)});
        menu.Children.Add(new TextBlock{Text="按研究内容分类，选择一项后按需读取。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8,0,8,12)});
        foreach(var category in NationalTeamWorkspaceCatalog.Categories)
        {
            var members=entries.Where(x=>x.CategoryKey==category.Key).ToArray();
            if(members.Length==0)continue; // 尚未实现的预警或市场周期不显示空入口。
            var group=new StackPanel{Margin=new Thickness(0,0,0,12)};
            group.Children.Add(new TextBlock{Text=category.Title,FontSize=15,FontWeight=FontWeights.SemiBold,Margin=new Thickness(7,2,0,3)});
            group.Children.Add(new TextBlock{Text=category.Description,TextWrapping=TextWrapping.Wrap,FontSize=11,
                Foreground=SystemColors.GrayTextBrush,Margin=new Thickness(7,0,6,5)});
            foreach(var item in members)
            {
                var button=new Button{Content=item.Title,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(10,7,7,7),
                    Margin=new Thickness(4,2,4,2),ToolTip=item.EvidenceNotice,Tag=item.RouteKey};
                AutomationProperties.SetName(button,category.Title+" · "+item.Title);
                var route=item.RouteKey;
                button.Click+=(_,_)=>OpenRoute(route);
                buttons.Add(route,button);
                group.Children.Add(button);
            }
            menu.Children.Add(group);
        }
        var left=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=menu};
        Grid.SetColumn(left,0);root.Children.Add(left);

        var right=new Grid{Margin=new Thickness(10,0,0,0)};
        right.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        right.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        var header=new StackPanel{Margin=new Thickness(4,0,0,6)};
        header.Children.Add(title);header.Children.Add(notice);
        Grid.SetRow(header,0);right.Children.Add(header);
        content.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        content.VerticalContentAlignment=VerticalAlignment.Stretch;
        Grid.SetRow(content,1);right.Children.Add(content);
        Grid.SetColumn(right,1);root.Children.Add(right);
        Content=root;
        title.Text="选择左侧研究功能";
        notice.Text="市场数据、行业估值与机构持仓属于不同证据链；页面切换不触发采集，也不会给出自动买入建议。";
        content.Content=new TextBlock{Text="请选择一项已上线的研究功能。尚未完成真实数据与有效性验证的功能不会以可用菜单出现。",
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,16,4,16)};
    }

    public void OpenRoute(string routeKey)
    {
        if(!items.TryGetValue(routeKey,out var item))throw new InvalidDataException("国家队研究功能路由不存在。");
        if(SelectedRouteKey==routeKey)return;
        // 先建成新页再交换；页面构造异常不应使旧页消失或留下错误高亮。
        var page=item.CreateView()??throw new InvalidDataException("插件研究功能无法创建页面。");
        content.Content=page;
        SelectedRouteKey=routeKey;
        title.Text=item.Title;
        notice.Text=item.EvidenceNotice+" 本页为独立研究资料，不能直接替代其他分类的证据。";
        foreach(var (key,button) in buttons)button.FontWeight=key==routeKey?FontWeights.SemiBold:FontWeights.Normal;
    }
}
