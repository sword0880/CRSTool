# 雪球观点追踪

插件 ID：`crs.xueqiu-watch`。

本模块只跟踪用户明确添加的雪球作者公开动态，并以股票为中心展示原帖提及和作者观点。不扩展为全网新闻或多来源情报订阅。

## 数据采集

- Worker 通过 `XueqiuPostApi` 读取指定作者的公开动态时间线，并调用雪球原帖详情 API 核对正文。
- 新作者默认回补最近 30 天；已取得的分页结果先行保存，单页失败不会丢弃前面成功取得的动态。
- 原帖 URL 规范化为 `https://xueqiu.com/{authorId}/{postId}`，并校验作者 ID 与观察名单一致。
- HTTP 429 按服务端重试时间退避；HTTP 567 暂停 API 读取 6 小时。不会切换到外部 RSS 作为替代来源。
- 任务网络沿用「任务中心」中的直连或 Mihomo 节点设置，手动执行与定时执行使用同一线路。

本模块不再包含 Google Alerts、Google RSS、Google 账号登录、浏览器自动化或独立 RSS 代理配置。公共 `CRS.Feed` 基础库仍可供其他模块复用，但雪球插件不依赖它。

## 页面使用

- 顶部同一行提供股票搜索、时间范围、作者筛选、刷新、设置作者和发言线索入口。
- 股票按 A 股、港股、美股分组；点击股票后，明细固定显示「原帖提及」。
- 双击明细内容可打开雪球原帖；选择记录后可查看已核验的原帖正文。
- 作者可添加、暂停、恢复或移除；新增或恢复作者时可回补最近 30 天动态。

## 数据与安全

Core 定义观点和采集协议；WPF 负责交互；Jobs 管理独立 SQLCipher 数据库 `opinions.db`。私人资料通过 Windows 管理员管道访问，保存时校验修订号并按请求标识去重。数据库升级保留作者、观点、已发现线索和正文历史，并清理旧版 Google/RSS 订阅表及作者 Feed 字段。

## 验证

使用 [Worker 开发打包脚本](../../../tests/Publish-BackgroundService.ps1) 生成候选包，再运行：

- `CRS.Tests.Background --xueqiu-private-data <候选包目录>`
- `CRS.Tests.Background --xueqiu-timeline`
- `CRS.Tests.Background --xueqiu-backfill`
- `CRS.Tests.Background --xueqiu-original-page-fixture`

桌面加载使用 `CRS.Tests.PluginHost --load-probe`。
