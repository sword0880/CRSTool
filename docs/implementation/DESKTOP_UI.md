# 桌面界面、设置与插件导航

<a id="national-team-compact-header"></a>
<a id="plugin-navigation-ui"></a>
<a id="product-brand"></a>
<a id="settings-navigation"></a>
<a id="ui-arch-remediation"></a>

## 品牌与公共组件

产品名称“小财牛”、口号“财富有数，投资有度”由`ProductIdentity`集中定义；图标使用交付素材。工程、服务、协议、数据路径和密钥保护常量继续使用既有技术标识，不因显示名称调整而改变。

色彩、焦点、按钮、表格和状态样式只维护[品牌视觉规范](BRAND_VISUAL_SYSTEM.md)。公共时间和文档机制见[北京时间](BEIJING_TIME.md)及[文档组件](DOCUMENTS.md)，不在各页面复制实现。

## 设置与独立窗口

设置使用两级分类、关键词搜索、独立滚动内容及底部固定保存区。分类元数据集中在`SettingsViewModel.Navigation.cs`；搜索只查分类元数据，不扫描用户输入或凭证。零匹配清空当前页；切换分类保留同一控件实例及草稿。

数据管理包含数据库与备份、运行资料清理；安全包含登录与验证、文档加密；通知包含邮件、钉钉和个人微信；运行与诊断提供桌面日志。通用保存不暗中发送测试通知，专项操作使用明确按钮。

数据库与备份管理使用独立窗口和独立模型，只处理当前目录、备份与恢复，不复用登录窗口。登录与验证、一次性二维码和认证恢复保留在其所属流程。详见[数据库与备份](DATABASE_BACKUP_MANAGEMENT.md)、[资料清理](RUNTIME_DATA_CLEANUP.md)和[认证指南](../guides/AUTH_IMPLEMENTATION.md)。

密码控件归各自名称域持有，父窗口统筹锁定、关闭和取消后的清理。表单拆分不改变保存、取消、焦点、授权和业务计算边界；合成截图不能代替实机凭证生命周期审计。

## 主窗口与插件页面

内置页面、插件页、任务中心及服务状态互斥显示；侧栏折叠后内容偏移实时跟随，返回普通页释放扩展页引用。先完成导航再异步刷新，迟到结果不能覆盖后来选择的页面。

插件管理中的版本来自宿主已核验并选中的包。启用/禁用配置表示下次启动设置，写入成功后才更新待生效显示；不把它伪装为本次运行状态。保留键盘焦点、高对比选中、无障碍名称和有界列表布局。

国家队仅提供一个工作台入口，业务路由由`NationalTeamWorkspaceCatalog`登记、按需创建。菜单通过`IPluginShellHeader`移交同一实例至`TitleBar.Header`，不在正文重复一份；离开插件或锁定后清除顶栏引用。不能用标题栏同级覆盖层替代原生交互Header，否则原生命中可能将菜单当拖动区。

工作台集中“研究说明”按钮及空声明面板已移除；具体页面中的缺失、未核验和口径提示继续保留。ETF采用紧凑页头、工具栏及可展开完整说明；日期用短格式，表格使用剩余空间而非无限外层滚动。

## 验证和限制

静态门禁`tests/Verify-WpfDesignSystem.ps1`检查XAML、资源键、颜色基线和日期格式。实际窗口入口包括WPF的`--settings-ui`、`--database-backup-ui`、`--data-cleanup-progress-ui`、`--compact-navigation`、`--plugin-header-native`及`--plugin-management-preview`，以及SectorUi的`--navigation`。

标题栏命中、鼠标/键盘焦点及辅助功能需要对应的原生窗口验证。普通Window中调用事件、合成截图或虚拟Invoke不能证明标题栏真实可点击；缺少交互前台时应保留未完成状态。桌面开发包同步不代表当前已运行窗口已经加载新代码，也不代表已安装Worker已更新。

## 状态栏简化（2026-11-06）

按用户确认移除右下角“最近同步”入口及专用历史轮询；状态栏保留服务健康、运行/等待数量和异常入口。同步执行的完成时间仍可在任务中心的执行记录中查看。相关验收记录位于 `artifacts/footer-without-recent-sync-20261106/`。

历史与导入页面高度绑定修复（2026-11-06）：页面创建时不再通过FindAncestor立即查找NavigationView；共享NavigationPageLayout在Loaded后绑定导航滚动视口的实际高度，Unloaded时释放宿主引用。保留历史表格与导入表单的内部滚动约束，避免移除高度约束后长表单撑高整页。隔离真实WPF窗口在1040×700、1280×880下验证分页、滚动、页面切换和独立创建；绑定错误日志为空。验证记录位于artifacts/page-height-binding-20261106/layout-final.log，截图使用合成数据。
