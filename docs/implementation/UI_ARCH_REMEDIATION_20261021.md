# UI-ARCH｜WPF 主程序与插件设计系统分阶段整改

> 日期：2026-10-21。范围：`CRS.Desktop.Wpf` / `CRS.Plugin.Wpf` / `CRS.Plugins.NationalTeamWatch` / Windows UI 测试。
>
> 本文记录**源码层实施状态**，未通过 Windows CI / 实机验收前不得宣称 UI 已全部整改完成。业务计算、行情证据、审核和投递规则不属于本项目。

## 总则

- 统一使用已交付的“小财牛” Logo、暖白 + 橘红品牌；正式品牌色由 `BrandPalette.xaml` 唯一定义。
- 品牌色和**业务状态色**分开：研究预览、审核失败、数据缺失及机构增减不使用同一橘红表示。
- WPF 框架继续使用 WPF-UI 4.3.x，数据表格保留 WPF DataGrid / DataGridExtensions，图表保留 LiveCharts；不引入第二套 UI 框架。
- 插件程序集**不能引用** `CRS.Desktop.Wpf`；由宿主在 `Application.Resources` 通过 `BrandPalette.xaml` 和 `SharedStyles.xaml` 提供动态资源。
- 页面只处理 UI 绑定与状态，不为迁移重新实现 Worker、行情数据库、插件任务、税务、加密和通知逻辑。

## 阶段一：共享设计系统（源码已实现，待 CI 验证）

1. 新增 `BrandStatusInfo/Success/Warning/Error*Brush`、`BrandTextHintBrush` 语义资源。
2. `SharedStyles.xaml` 新增下列公开资源键：
   - 文本与版式：`UiPageRoot`、`UiPageTitle`、`UiSectionTitle`、`UiBodyText`、`UiHintText`、`UiStatusText`、`UiStatusWarningText`。
   - 容器：`UiSurfaceCard`、`UiStatusBanner`、`UiToolbar`。
   - 操作：`UiPrimaryButton`、`UiSecondaryButton`、`UiGhostButton`、`UiDangerButton`。
   - 表格：`UiReadOnlyDataGrid`、`UiFilterableDataGrid`、`UiDenseDataGrid`、`UiEditableDataGrid`。
3. `CRS.Plugin.Wpf/PluginUiResources.cs` 只声明公开资源契约（v1.0）；主窗口在插件加载前、进入插件页前与候选升级页验证前检查资源是否存在且类型正确，失败时中止进入插件工作区。插件不能自行复制整套配色。
4. 兼容策略：暂保留原有默认 `DataGrid`、`BrandButton` 等隐式样式，避免一次性改变尚未迁移页面的行为。新页面应显式选择只读/可筛选/可编辑表格，后续逐页迁移后再考虑收紧隐式默认。

## 阶段二：国家队插件样板（源码部分实现，待交互/视觉验收）

- ETF 根视图不再使用贯穿所有页签的外层 ScrollViewer；主 Tab 区使用可用高度，监控与配置页签内部单独滚动。概览与配置表格限制最高 480px，不让全部内容无限扩展。
- 覆盖率与审核页签使用剩余空间布置 DataGrid，而不是对主容器强制 640px。
- `ScrollBoundaryBehavior` 在 `CRS.Plugin.Wpf` 统一封装表格到达滚动边界后的父容器滚动转交，删除 ETF/减持预警的重复代码。
- ETF、直接持股与减持预警接入宿主 `UiPageRoot`、标题、状态提示和表格语义样式。
- 保留所有命令、绑定字段、官方证据链接白名单、市场数据和研判规则。

**仍需后续验证与处理：** ETF 份额/成交额图表高度目前仍有 400px / 170px 固定值；复杂管理表单仍使用较多固定输入宽度；需要 Windows 真实窗口确认内容可达性后再做细分响应式整改。

## 阶段三：主程序大型页面（部分实现，其余严格待办）

- [x] 绩效页不再通过 `FindName("Navigation")` 从主窗口检索高度；内部 Grid 改为伸展布局。
- [x] 插件管理页移除重复定义的四套页面级 ControlTemplate；表格行、列头、单元格、操作按钮迁移到 `SharedStyles.xaml` 语义样式，保留默认控件模板。
- [x] 设置页基础标题、提示文字和白色面板接入共享样式/颜色。
- [ ] **设置页拆分**：存储、安全与 TOTP、文档口令、邮件、钉钉、企业微信、日志拆成多个独立 UserControl，以 SettingsSectionHost 切换。需要先把密码框的 `PasswordChanged` 回调和锁定后的清理职责拆为明确的安全接口；不得丢失 `ClearSecurityInputs`、`ClearDocumentInputs`、`ClearEmailInput`、`ClearDingTalkInputs`、`ClearWeChatInputs`，也不得重建时复用明文凭证。
- [ ] 绩效页进一步拆出账户范围、图表、补录、资金流和历史的模板/子视图；在确立数据上下文和尺寸回归后实施。
- [ ] 插件管理页在 Windows 上检查表格排序/键盘选中/虚拟化以及鼠标焦点；若默认模板带来变化，先修复公共样式再合并。

## 阶段四：长期质量门禁（CI/截图基础已实现，未验证通过）

- `tests/Verify-WpfDesignSystem.ps1`：XML 完整性、`Brand*/Ui*` 动态资源键存在性、插件无反向引用、硬编码 HEX 颜色不超过现存基线。
- `tests/wpf-color-baseline.json`：旧页面有意保留的硬编码次数债务。**新增页面基线为零**；减少旧硬编码后应同步下调基线，不得上调以掩盖回退。
- `tests/CRS.Tests.Wpf --shell-brand-preview <目录>`：主窗口 1040 / 1280 / 1440 宽度、展开/折叠，96 / 120 / 144 DPI 位图。
- `tests/CRS.Tests.PluginRecovery --ui-visual-preview <目录>`：ETF、直接持股、减持预警三种空状态插件 UI，三宽三 DPI，禁用 Loaded 取数，不连接 Worker。
- `.github/workflows/wpf-ui-design-checks.yml`：Windows .NET10 静态检查、解决方案编译、合成视觉预览与截图 Artifact。当前无人工审核批准的黄金截图，**只生成可审阅图片，不做未经校准的像素差 CI 阻断**。
- [ ] 增加真实 Windows 会话回归：100% / 125% / 150% 的系统缩放及多显示器切换、窗口最大化、导航收起/展开、键盘 Tab/focus、表格读写/筛选/排序、嵌套滚轮、图表重置、长中文文本、空/忙/错/数据质量状态、插件停止/重载/升级、设置关闭/锁屏后的秘密清理。
- [ ] CI 首次执行成功后筛选并批准黄金截图，设定可维护的差异门槛；不依赖单纯像素变化判断所有交互正确。

### Windows 建议验收命令

```powershell
./tests/Verify-WpfDesignSystem.ps1
dotnet restore CRS.sln
dotnet build CRS.sln -c Release --no-restore -m:1
dotnet run --project tests/CRS.Tests.Wpf -c Release --no-build -- --shell-brand-preview artifacts/ui-preview/main
dotnet run --project tests/CRS.Tests.PluginRecovery -c Release --no-build -- --ui-visual-preview artifacts/ui-preview/plugins
./tests/Verify-Architecture.ps1
```

以上截图验收为**静态布局与合成空状态测试**，不会验证后台数据的真实性，也不能代替实机窗口、键盘和凭证安全生命周期检查。

## 合并纪律

- 保留分阶段独立 commit，PR 在 Windows 编译和截图验收前必须为 Draft。
- 任一批改动若导致插件不能卸载、页面内容不可达、缺失证据被当作零、口令残留或 WPF 模板键盘行为回归，撤回该批 UI 改动，不绕过业务规则。
- 设置拆分与 LiveCharts 图表颜色令牌属于后续专项；不因为界面任务修改来源数据、税务计算或国家队预警策略。
