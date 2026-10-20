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
