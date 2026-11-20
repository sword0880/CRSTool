# 国家队与市场研究插件

插件 ID 为 `crs.national-team`。解决方案中的 `src → Plugins → NationalTeam` 收录同一个插件的三个项目。

| 项目 | 职责 |
| --- | --- |
| [CRS.Plugins.NationalTeam.Core](CRS.Plugins.NationalTeam.Core/CRS.Plugins.NationalTeam.Core.csproj) | 领域模型、业务规则、接口与数据契约 |
| [CRS.Plugins.NationalTeam.Jobs](CRS.Plugins.NationalTeam.Jobs/CRS.Plugins.NationalTeam.Jobs.csproj) | Worker 入口、后台采集和查询、市场数据存储与迁移 |
| [CRS.Plugins.NationalTeamWatch](CRS.Plugins.NationalTeamWatch/CRS.Plugins.NationalTeamWatch.csproj) | WPF 入口、页面、ViewModel 和本机管道客户端。桌面和后台入口声明见 [plugin.json](CRS.Plugins.NationalTeamWatch/plugin.json) |

桌面经本机管道查询数据并提交管理命令。Worker 管理插件数据和后台任务，桌面与后台共用 Core 的业务契约。

从解决方案构建项目。桌面和 Worker 的发行流程共用[国家队插件打包脚本](../../../tests/Publish-NationalTeamPackage.ps1)。目录与功能归属由[布局检查](../../../tests/Verify-NationalTeamFeatureLayout.ps1)验证，业务回归和隔离加载入口见[测试说明](../../../tests/README.md)。

业务文档从[国家队与市场研究模块](../../../docs/modules/national-team/README.md)进入。
