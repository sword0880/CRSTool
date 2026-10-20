using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace CRS.DesktopClient.ViewModels;

/// <summary>设置分类目录仅描述展示与检索，业务保存仍使用原有应用接口。</summary>
public sealed partial class SettingsSection(string key,string group,string title,string description,string keywords):ObservableObject
{
    public string Key {get;}=key;
    public string Group {get;}=group;
    public string Title {get;}=title;
    public string Description {get;}=description;
    public string Keywords {get;}=keywords;
    public string Breadcrumb=>$"所有设置  >  {Group}  >  {Title}";
    [ObservableProperty] private bool isSelected;
}

/// <summary>目录分组只包含已经实现的页面，后续通知渠道可直接登记为同级子页。</summary>
public sealed partial class SettingsGroup(string title,IReadOnlyList<SettingsSection> children):ObservableObject
{
    public string Title {get;}=title;
    public IReadOnlyList<SettingsSection> Children {get;}=children;
    [ObservableProperty] private bool isSelected;
}

public partial class SettingsViewModel
{
    // 新分类在此登记标题、说明和检索词；分类切换不重新加载表单，保留未保存的输入。
    public IReadOnlyList<SettingsSection> Sections {get;} =
    [
        new("storage","数据管理","数据库与备份","管理本机数据位置、备份与恢复。","数据 存储 目录 路径 主密码 手机绑定"),
        new("security","安全","登录与验证","管理当前保险库的登录验证方式。","安全 手机 Windows Hello PIN 指纹 人脸 主密码 验证码"),
        new("documents","安全","文档加密","统一管理文档导出与解密所用的密码。","文档 导出 密码 解密"),
        new("notifications","通知渠道","邮件","配置后台通知使用的邮件发送渠道。","邮件 SMTP 授权码 密码 收件 发件 通知 端口 TLS STARTTLS SSL 服务器"),
        new("dingtalk","通知渠道","钉钉","配置钉钉群机器人通知。","钉钉 机器人 Token 加签 通知"),
        new("wechat","通知渠道","个人微信","通过 PushPlus 服务接收个人微信提醒。","微信 PushPlus Token 个人 通知"),
        new("logging","运行与诊断","桌面日志","调整桌面日志的保存位置与保留规则。","日志 级别 归档 调试 Debug")
    ];
    public ObservableCollection<SettingsSection> VisibleSections {get;}=[];
    public ObservableCollection<SettingsGroup> NavigationGroups {get;}=[];
    [ObservableProperty] private SettingsSection? selectedSection;
    [ObservableProperty] private string sectionSearch="";
    public bool NoSectionMatches=>VisibleSections.Count==0;
    public bool HasSelectedSection=>SelectedSection is not null;

    private void InitializeNavigation()=>FilterSections();
    partial void OnSectionSearchChanged(string value)=>FilterSections();
    partial void OnSelectedSectionChanged(SettingsSection? value)
    {
        foreach(var section in Sections)section.IsSelected=section==value;
        OnPropertyChanged(nameof(HasSelectedSection));
    }
    private void FilterSections()
    {
        var previous=SelectedSection;
        var terms=SectionSearch.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var matches=Sections.Where(section=>terms.All(term=>
            (section.Group+" "+section.Title+" "+section.Description+" "+section.Keywords).Contains(term,StringComparison.OrdinalIgnoreCase))).ToArray();
        VisibleSections.Clear();foreach(var section in matches)VisibleSections.Add(section);
        NavigationGroups.Clear();foreach(var group in matches.GroupBy(section=>section.Group))NavigationGroups.Add(new(group.Key,group.ToArray()));
        // 零匹配时清空右侧，避免把上一分类误认为搜索结果；清除搜索不会丢失表单内容。
        SelectedSection=matches.Contains(previous)?previous:matches.FirstOrDefault();
        OnPropertyChanged(nameof(NoSectionMatches));
    }
}
