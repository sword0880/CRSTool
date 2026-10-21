# 小财牛全局视觉规范（2026-10-20）

## 单一来源
- 认证窗口现有暖白底与橘红色是品牌基线，强调色 #C13D24；其他功能页和插件不自行定义蓝色主色。
- 品牌色值只允许在 `src/CRS.Desktop.Wpf/Styles/BrandPalette.xaml` 中定义，使用 `{DynamicResource BrandAccentBrush}` 等语义资源。
- `SharedStyles.xaml` 是公共按钮、卡片、页面标题、指标卡和表格的唯一默认样式来源。局部 XAML 可以调整布局、尺寸、业务状态，但不得复制整套按钮模板或单独配置另一套主色。
- `App.xaml` 按顺序加载 WPF-UI Light、Controls、BrandPalette、SharedStyles。启动时使用 BrandAccentBrush 初始化 WPF-UI 全局强调色；导航、主按钮等第三方控件因此继承统一强调色。
- 插件通过 Application.Resources 使用相同资源；不要在插件程序集放另一份品牌调色板。

## 语义角色
| 角色 | 资源 | 用途 |
|---|---|---|
| 主要操作 | BrandAccentBrush | 主要按钮、激活状态、关键可点击文本 |
| 聚焦与悬停 | BrandAccentDarkBrush / BrandBorderStrongBrush | 键盘焦点、悬停强调 |
| 页面背景 | BrandCanvasBrush | 主窗口、插件承载层、设置及辅助窗口 |
| 内容卡片 | BrandSurfaceBrush / BrandSurfaceAltBrush | 卡片、信息面板和次级表面 |
| 低强调底色 | BrandAccentPaleBrush / BrandAccentSoftBrush | 摘要、说明、选中背景 |
| 分隔线 | BrandBorderBrush / BrandBorderSubtleBrush | 卡片、表格、列表分隔 |
| 品牌字体 | BrandTextPrimaryBrush / BrandTextSecondaryBrush / BrandTextMutedBrush | 主要、辅助和弱提示文字 |

**状态色不一刀切**：错误、成功、风险、预警等必须保留语义区别，不能将所有绿色或红色业务标记替换成品牌橘红；投资图表的多序列数据色也不强行统一。

## 修改纪律
1. 新增页面优先直接使用 PageTitle、Subtitle、Card、MetricButton、ActionButton、PrimaryAction。
2. 禁止再使用旧蓝色主色 #2563EB、#EFF6FF、#DBEAFE、#F0F5FF、#1E3A8A 编写新页面；如确为金融图表序列色，应写明其数据语义。
3. 禁止在业务页面重新创建整套 Style/ControlTemplate 用于主按钮；确需特殊控件请抽出 SharedStyles 中的语义样式。
4. 如修改品牌颜色，仅更新 BrandPalette.xaml。WPF-UI 全局主色从 BrandAccentBrush 动态读取，不重复定义十六进制色值。

## 主窗口左上角品牌栏（方案 A · 2026-10-21）

- `MainWindow.xaml` 使用 52px 标题栏，左上角单行排列：36px 可点击侧栏切换按钮、30px 现有透明牛头 Logo、16px「小财牛」。主界面不再重复显示口号；认证页口号与交付素材保持不变。
- 品牌区通过 `Navigation.IsPaneOpen` 统一切换宽度：展开 206px、收起 56px。收起只保留切换按钮，不强行压缩图文；侧栏主体继续交给 WPF-UI `NavigationView`。
- 隐藏 NavigationView 自带折叠按钮，避免出现两个入口；标题栏按钮只反转同一个 `IsPaneOpen`，已有 `PluginSurface` 偏移、内置页面及插件互斥规则沿用该状态。不要为某插件复制侧栏布局。
- 点击区域只覆盖真实按钮，`WindowChrome.IsHitTestVisibleInChrome` 确保标题栏按钮能接收点击；Logo 与品牌文字不拦截系统标题栏拖拽。保留系统最小化/最大化/关闭按钮。
- 切换按钮样式唯一来源为 `SharedStyles.xaml` 的 `BrandNavigationToggle`，含鼠标悬停、按下、键盘焦点与禁用态。折叠/展开的动态提示和箭头在 `MainWindow.xaml` 根据 `Navigation.IsPaneOpen` 切换，不引入额外主题色。
- WPF 脱离窗口生命周期的视觉回归：`dotnet run --project tests/CRS.Tests.Wpf -c Release -- --shell-brand-preview artifacts/brand-header-preview`。分别输出 1280/1040 宽度、展开/收起截图，并检查按钮点击、品牌图文、插件偏移及拖拽命中。**该预览不能替代 Windows 实际显示/窗口拖拽、DPI、最大化及插件会话实测。**

## 验收
- 身份认证、工作区侧栏与导航选中、概览指标卡、数据导入、历史、税务、绩效、插件管理、设置、日志窗口、任务编辑窗口视觉一致。
- 按钮默认/悬停/按下/键盘焦点/禁用状态可辨识；主按钮白字与深橘底对比清晰。
- 表格行选中时文字可读，滚动条可拖动，长日志仍可复制；Windows Hello/TOTP/锁定/导入/计算命令不因样式变更而受影响。
- 插件页面在主窗口导航折叠/展开、不同 DPI 与窗口尺寸下保持可用。
- **未完成 Windows WPF 实际运行验证前，不能标记视觉验收通过。**
