# 系统设置分类导航

日期：2026-10-19。依据现有 WPF 设置窗口及用户提供的 Visual Studio 选项窗口参考实现。

## 布局与扩展

窗口采用左侧两级树目录、分类关键词搜索、右侧当前位置与独立滚动设置页、底部固定保存区。默认 1040×780，最小 860×640，支持最大化。

| 分组 | 已实现页面 |
| --- | --- |
| 数据管理 | 数据库与备份 |
| 安全 | 登录与验证、文档加密 |
| 通知渠道 | 邮件 |
| 运行与诊断 | 桌面日志 |

分类元数据集中在 `SettingsViewModel.Navigation.cs` 的 `Sections` 中，包含稳定键、分组、标题、说明和检索词。树目录由同一目录生成；新增页面需登记元数据并添加对应展示内容。搜索按多个空格分隔的词同时匹配分类标题、分组、说明和关键词，不扫描用户输入值或凭证。零匹配时清空右侧并展示提示。

未来钉钉、微信可作为“通知渠道”下的独立子页；当前没有注册未实现的入口。渠道发送仍由公共通知接口及适配器负责，界面不包含发送实现。

## 操作与安全

切换分类仅改变可见区域和滚动位置，不重新加载或销毁表单，因此保留未保存输入。固定的“保存设置”继续按既有逻辑处理跨分类改动；分组内的验证、文档密码、邮件保存仍保留专用操作。保存不会触发测试邮件，测试必须显式点击。邮件页将保存、测试、刷新和状态放在表单顶部。

登录切换仍要求既有凭证验证，未解锁时仅显示解锁提示。锁定和关闭继续清空密码框及模型中的临时凭证。数据库管理仍通过既有工作流打开；切换目录不自动迁移或创建数据库。日志配置保存后立即生效，数据库目录下次启动生效。

## 验证

`CRS.Tests.Wpf --settings-ui <输出目录>` 使用真实 WPF 窗口及隔离设置/保险库替身，验证树节点选择、SMTP 关键词定位、零匹配清空、目录恢复、切换草稿保留、最小窗口保存区与锁定凭证清理，并输出各分类截图。此检查不访问个人数据库，也不发送邮件。

修改前截图：`artifacts/settings-before/settings.png`。修改后截图：`artifacts/settings-redesigned/`。构建与验证日志：`artifacts/settings-redesign-build.log`、`artifacts/settings-redesign-ui.log`、`artifacts/settings-redesign-regression.log`。实际执行结果以日志为准。

Debug设置窗口及Release桌面宿主构建均为0警告、0错误；既有WPF交互回归与架构门禁通过。新版桌面开发包为 `artifacts/desktop-settings-tree-final-20261019`，其ETF插件与配套Worker包的52项文件哈希一致（`artifacts/settings-redesign-pair-check.log`），并已同步Debug/Release开发插件目录。Release构建与同步日志分别为 `artifacts/settings-redesign-release-build.log`、`artifacts/settings-redesign-sync.log`。
