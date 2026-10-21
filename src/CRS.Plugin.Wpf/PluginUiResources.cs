using System.Windows;
using System.Windows.Media;

namespace CRS.Plugin.Wpf;

/// <summary>
/// 插件 UI 只能消费宿主资源，不能引用桌面程序集或携带第二套品牌调色板。
/// 应由宿主在加载插件时和候选升级验证前调用；仅验证视觉契约，不执行插件业务。
/// </summary>
public static class PluginUiResources
{
    public const string ContractVersion = "1.0";

    /// <summary>跨进程包升级时不要轻易更名；旧插件可继续使用已发布的资源键。</summary>
    public static IReadOnlyList<string> RequiredBrushes { get; } =
    [
        "BrandAccentBrush", "BrandCanvasBrush", "BrandSurfaceBrush", "BrandBorderBrush",
        "BrandTextPrimaryBrush", "BrandTextSecondaryBrush", "BrandTextHintBrush",
        "BrandStatusInfoBrush", "BrandStatusWarningBrush", "BrandStatusErrorBrush"
    ];

    public static IReadOnlyList<string> RequiredStyles { get; } =
    [
        "UiPageRoot", "UiPageTitle", "UiSectionTitle", "UiBodyText", "UiHintText",
        "UiStatusText", "UiStatusBanner", "UiSurfaceCard", "UiToolbar",
        "UiReadOnlyDataGrid", "UiFilterableDataGrid", "UiDenseDataGrid"
    ];

    /// <summary>ResourceDictionary 索引器可查询合并资源字典；资源必须具有正确的 WPF 类型。</summary>
    public static void Validate(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach (var key in RequiredBrushes)
            if (resources[key] is not Brush)
                throw new InvalidOperationException("插件 UI 宿主缺少有效画刷资源：" + key);
        foreach (var key in RequiredStyles)
            if (resources[key] is not Style)
                throw new InvalidOperationException("插件 UI 宿主缺少有效控件样式：" + key);
    }
}
