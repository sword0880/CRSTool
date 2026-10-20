# 公共通知渠道边界

更新日期：2026-10-20。依据用户要求：邮件、钉钉及个人微信在系统设置配置；发送属于公共组件，并按职责分目录。

## 调用方向

业务预警 → `IBackgroundNotificationContext` → Worker薄适配 → `NotificationDispatcher` → `INotificationChannel`适配器。

`CRS.Notifications`不依赖Worker、WPF或具体业务。Worker负责装配和管理操作授权。业务只提供消息内容、渠道ID和配置Profile，不取得收件人、Webhook或授权码。WPF经应用用例和受限IPC读写设置，不连接通知提供商。

## 接口约定

| 内容 | 契约与责任 |
| --- | --- |
| 业务消息 | `NotificationEnvelope`包含稳定MessageId、标题、正文、分类及少量非秘密附加字段；提交时冻结附加字段 |
| 渠道 | `INotificationChannel`实现配置状态查询与发送，自行处理提供商格式、凭据、目标、限流及错误映射 |
| 配置 | `profileId`是受保护配置的引用，例如default；不是地址、电话号码或Webhook |
| 路由 | 显式指定channelId与profileId；不因失败而自动转发其它渠道 |
| 注册 | 渠道ID唯一，重复注册直接失败，避免覆盖已有适配器 |
| 受理结果 | Accepted只表示提供商受理，不证明到达或已读；ProviderReceipt只能使用提供商真实返回值 |
| 安全重试 | RetryableFailure必须能证明此次没有被受理；提交后连接中断应为OutcomeUnknown，禁止仅因超时就自动重发 |
| 提供商能力 | 描述符可声明回执及提供商幂等能力，默认均false；能力标志不等于系统已实现对应流程 |

未安装渠道返回Unavailable；明确无效的消息在调用适配器前返回PermanentFailure；未分类的适配器异常、串单结果保守记为OutcomeUnknown。一个渠道状态读取异常不抹去其它渠道的状态。调用方取消后不再开始新提交；已经开始提交的取消仍须由投递流程按不确定结果保留。

事件、Outbox与重试属于业务投递流程，公共组件不另建数据库或调度器。Outbox的去重键须包含事件变更版本、channelId和profileId；同一投递重试沿用MessageId，但不能把本地MessageId当作提供商幂等承诺。切换订阅不能追溯重发历史事件，除非用户明确要求。

## 新增钉钉或微信适配器

1. 实现`INotificationChannel`，使用独立渠道ID；将提供商响应映射为统一状态。
2. 通过独立配置模型保存该渠道的凭据和目标，在系统设置增加该渠道的配置入口；不向邮件配置追加Webhook等无关字段。
3. 在Worker组合根注册适配器，保持Worker仅负责装配、契约转换和授权。
4. 业务订阅选择该渠道及Profile，业务算法与事件证据模型无需改动。
5. 验证停用、限流、明确拒绝、提交结果未知及稳定MessageId；只有具备真实证据才能声明送达/幂等能力。

## 当前实现范围

邮件为第一个实际适配器，当前支持default配置；系统设置默认关闭，保存不发信，测试需用户显式点击。邮件配置使用DPAPI保护并受宿主目录权限约束，已保存口令不返回桌面。SMTP强制TLS及系统证书校验。

钉钉已实现自定义机器人文本消息及加签，ID为dingtalk；个人微信先使用PushPlus服务，ID为wechat，不是企业微信群机器人。两个通道均只支持default配置，Worker组合根注册，系统设置增加独立页面。配置经管理员IPC传递，不进入持久任务命令；DPAPI保护Token及钉钉加签密钥，快照只显示是否已设置，修订号防旧窗口覆盖。需要在业务订阅中明确选择新渠道，保存系统配置不会切换现有订阅或补发历史。

目录按Contracts、Dispatching、Configuration、Transport及Channels/Email、DingTalk、WeChat划分，见[组件目录说明](../../src/CRS.Notifications/README.md)。HTTP地址固定HTTPS，生产传输禁用重定向与自动重试，正文有UTF8字节限制，通道串行并有3秒本地间隔，HTTP429尊重Retry-After。该限流不是跨重启的持久提供商配额。

PushPlus返回真实流水号时保留ProviderReceipt；尚未实现回执查询或提供商幂等。提交后取消、超时、5xx或无法解析响应保留OutcomeUnknown。隔离HTTP与SMTP测试不代表真实微信/钉钉送达，未使用真实Token、未部署已安装服务。
