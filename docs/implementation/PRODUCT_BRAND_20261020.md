# 小财牛品牌名称同步（2026-10-20）

产品中文名：**小财牛**。品牌口号：**财富有数，投资有度**。

面向用户的名称统一用于主窗口、解锁页、启动错误提示、Windows Hello验证说明、Windows文件产品属性和新生成的验证器绑定标签。主窗口标题区和解锁页展示口号；运行时文案由`ProductIdentity`集中定义。认证组件仅同步新绑定显示名称，既有验证码密钥和已绑定手机条目不作迁移。

Windows后台服务显示名为“小财牛后台服务”，安装脚本和服务设计文档同步。服务技术标识仍为`CRSBackgroundService`，显示名修改不重启服务、不更改数据路径或服务身份。

邮件组件的发件人显示名与测试邮件主题同步使用“小财牛”；此项是源码与构建更新，实际服务邮件组件随下次配套发行更新。本轮未发送真实邮件，现有邮件配置不因改名而启用。

工程名称、程序集、命名空间、现有数据目录、插件与Windows服务技术标识继续使用CRS，避免显示名称调整引起数据定位或协议变化。

文档同步范围：根README、文档导航、产品说明、发布计划、安全设计、安全认证规范、认证实施说明，以及当前预警设计中的产品称呼。仓库URL、文件路径、规范名称中的技术标识及历史日志保留原值。

验证：Debug和Release桌面构建均0警告0错误，Windows产品名及文件说明均为“小财牛”；已有WPF交互检查通过。安全组件12项、TOTP标准向量/URI参数和独立导出加密检查通过，验证码算法与既有密钥未改变。证据位于`artifacts/brand-*-build.log`、`brand-*-final.log`、`brand-security-checks.log`和`brand-wpf-checks.log`。

公共通知组件23项隔离检查通过，仅使用模拟渠道和传输；安装脚本语法及架构门禁通过。对应证据为`artifacts/brand-notification-checks.log`和`brand-architecture.log`。


## 按图标交付说明接入

依据仓库`images/README_交付说明.md`接入已确认的红橙牛头/金币素材，没有重绘或改变造型。原素材保留在images；项目资源为`src/CRS.Desktop.Wpf/Assets/Brand/XiaoCaiNiu.ico`与`XiaoCaiNiu_256.png`，复制后哈希与交付原件完全一致。

- ApplicationIcon绑定多尺寸ICO，含16/24/32/48/64/128/256像素；作为WPF Resource一并编译。
- 五个现有窗口使用同一ICO，覆盖主窗口、身份验证、设置、计划编辑和执行日志等窗口；主窗口标题区及身份验证页使用256像素RGBA原图。
- 实际截图发现TitleBar默认图标槽会裁切32像素图标，已改为独立32像素品牌展示区域，鼠标事件穿透，保留拖动、最小化/最大化/关闭行为及窗口标题。
- 程序名、口号、运行身份、数据目录、协议和密钥常量不因图标改变。

验证：`brand-icons-verification.json`记录交付哈希、ICO七种尺寸及PNG外围透明检查；Windows实际EXE提取图标为`artifacts/brand-icons-exe.png`。最终Debug/Release构建分别见`brand-icons-debug-final.log`、`brand-icons-release-final.log`（均0警告/0错误）。使用合成隔离保险库的`--smoke-test --brand-icons --image ...`预览退出码0，最终截图为`brand-icons-final.png`及`brand-icons-final-vault.png`，已逐张查看。首轮全页面smoke预览未完成，不能计为全页面回归通过；品牌专用预览避免无关设置/财务流程。

本轮未替换任何已安装桌面程序或刷新系统图标缓存；新图标随本次构建/后续发布生效。

## 身份认证窗体品牌更新（2026-10-20）

身份认证窗体改用暖白背景、红橙色主操作及浅暖色验证区域；顶部展示68像素交付Logo、品牌名“小财牛”和口号“财富有数，投资有度”。安全访问标识与身份验证标题保留清晰层次，窗口标题也从ProductIdentity统一读取。图片继续使用交付的256像素透明PNG，未重绘或处理原图。

此项仅调整VaultWindow.xaml的展示，首次创建、TOTP、Windows Hello、恢复与维护的命令和显示条件不变；密码控件、一次性验证码及锁定清理沿用现有实现。长内容保留滚动支持。

实际合成隔离保险库预览退出码0，截图 artifacts/auth-brand-refresh-vault.png 已逐张查看；日志 auth-brand-smoke.log。未读取个人保险库，也未替换已安装桌面程序。

Debug/Release桌面构建均0警告、0错误（auth-brand-debug.log / auth-brand-release.log）；现有WPF交互回归通过（auth-brand-wpf-checks.log），覆盖TOTP/Hello、首次绑定、凭证清理、恢复确认与自动锁定。与修改前XAML比对的67项认证控件/绑定属性一致，记录为 auth-brand-contract-check.json。

## 2026-10-22 首次窗口尺寸与品牌区焦点

主窗口和认证窗口在首次创建原生窗口时，通过 `StartupWindowSizing` 读取所在显示器的工作区及实际 DPI。初始宽高和最小宽高均限制在工作区内，四周预留 16 DIP，避免固定 880 / 740 DIP 高度在小屏或高缩放下越界。主窗口仍可最大化或调整窗口。认证窗口使用 `ResizeMode="CanMinimize"`，禁止最大化及手动拉伸，保留最小化与关闭；长表单通过内部滚动查看。

认证窗口的品牌头部固定在表单滚动区上方，长二维码/恢复内容或输入框自动获得焦点时，不会把 Logo 滚出可见区域。

认证窗口禁止最大化的补充修改：XAML 属性核对通过；Release 构建 0 警告、0 错误（`artifacts/auth-no-maximize-release.log`）。Debug 构建因正在运行的桌面程序占用 EXE 而无法覆盖（`artifacts/auth-no-maximize-debug.log`），未关闭用户当前窗口；下次关闭并重新构建后生效。

`BrandNavigationToggle` 删除 `IsKeyboardFocused` 的红色边框触发器，避免点击品牌区切换导航后一直显示红框；保留透明背景、轻量悬停/按下反馈和原生键盘焦点提示。导航开关与品牌素材不变。

`CRS.Common/Time` 是已经接入的无 UI 公共时间实现：统一北京时间时钟、时间点偏移标准化、文本序列化和 JSON `DateTimeOffset` 边界。桌面、Worker、通知与插件共用公共实现，避免各层重复处理时间。现有职责不是未来插件功能占位，也不包含交易日历或市场行情。

验证：本次 Debug / Release 构建均为 0 警告、0 错误（`artifacts/startup-layout-*-build.log`）；主窗口三宽度、三 DPI 位图预览及导航命中回归通过，人工查看品牌局部图片无红框（`artifacts/startup-layout-brand/`）。认证 XAML 的绑定、命名控件与事件契约对比保持一致。完整隔离保险库启动预览在创建测试库阶段退出，未进入窗口检查；高缩放、多屏首次显示及认证窗口实际滚动仍待实机验收。界面规范脚本因未修改的 `BackgroundServicePage.xaml` 已有硬编码颜色 5 / 基线 2 未通过，未扩大本次修改范围。

## 2026-10-22 折叠导航分类浮层

折叠侧栏点击分类图标时，在其右侧打开带分类标题、原菜单图标及完整子项的浮层；只保留一个浮层，不改变侧栏宽度或内容区位置。选页后关闭，当前页面所属分类保持高亮，重新打开浮层时用勾选标识当前页面。无子项入口继续直接打开，所有顶级菜单保留悬停名称。

浮层使用原生 `ContextMenu`，沿用上下键、Enter 和点击外部关闭；Esc 关闭并将焦点送回分类图标，折叠态的 Enter、空格、右方向键可打开菜单。展开侧栏时关闭浮层，并使用互斥的内联分类树；当前分类随页面选择恢复展开。设置、锁定、窗口失去活动状态及关闭时清理浮层。只改变展示与导航，内置页面继续走现有页面缓存，动态插件和任务入口继续调用原菜单对象的处理器，并保留禁用状态。

实现集中在 `Views/MainWindow.Navigation.cs`，样式为 `CompactNavigationMenu` / `CompactNavigationMenuItem`，全部引用共享品牌资源；没有复制业务页面或引入新的 UI 框架。

验证：Release 构建0警告、0错误（`artifacts/compact-navigation-build.log`）；19项隔离交互检查通过（`artifacts/compact-navigation-checks.log`），原有1040/1280/1440宽度、96/120/144 DPI位图顶栏回归通过（`artifacts/compact-navigation-brand.log`）。人工查看 `artifacts/compact-navigation/` 中税务浮层、投资浮层与折叠主窗体截图。测试分离主窗口 Loaded，使用合成页面和动态入口，不启动正式插件或连接个人保险库；分类控件使用真实 OnClick 路由、菜单项使用自动化调用，覆盖路由鼠标事件和键盘事件，不等于物理鼠标、跨屏定位或实际插件生命周期验收。全局视觉规范检查仍因后台服务页既有硬编码颜色4次/基线2次未通过（`artifacts/compact-navigation-design-check.log`），本次新增样式未加入硬编码颜色。
