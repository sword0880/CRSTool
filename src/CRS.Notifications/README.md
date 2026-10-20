# 小财牛公共通知组件

只依赖BCL、SMTP库与Windows凭据保护，不依赖WPF、Worker或业务插件。命名空间统一保留 `CRS.Notifications`。

| 目录 | 职责 |
| --- | --- |
| Contracts | 消息、发送结果、通道接口、非秘密配置快照 |
| Dispatching | 通道注册、输入检查、明确路由及结果校验 |
| Configuration | Webhook通道的DPAPI配置保护、修订检查和原子保存 |
| Transport | HTTP提交、字节上限、限流与保守的结果映射 |
| Channels/Email | 邮件配置服务、SMTP传输及邮件适配器 |
| Channels/DingTalk | 钉钉自定义机器人HMAC-SHA256加签与文本请求 |
| Channels/WeChat | 个人微信PushPlus适配器 |

当前三个通道ID为 `email`、`dingtalk`、`wechat`，均使用 `default` Profile。
Worker组合根注册通道；业务只提交消息与渠道ID。WPF通过应用用例与管理员IPC管理配置，不访问秘密文件或HTTP接口。
新增提供商放在独立Channels目录并实现INotificationChannel，不把提供商逻辑放入Worker或WPF。

配置由宿主准备的受保护目录保存，Webhook凭据使用LocalMachine DPAPI，ACL由宿主负责。只返回凭据是否存在；空值保留、显式清除才删除，过期Revision拒绝覆盖。旧邮件文件名和格式不变。

设置默认关闭，保存不发送。钉钉填写access_token及SEC加签密钥，不填写完整Webhook URL。个人微信先在PushPlus绑定微信，再填写Token；消息会经过该第三方服务。
配置和测试入口见[设置指南](../../docs/guides/SETTINGS.md)。订阅还需在业务预警设置中选择渠道。

HTTP目标固定HTTPS且生产传输禁用重定向。每通道串行，单进程提交间隔至少3秒，HTTP 429尊重Retry-After；没有持久提供商配额与送达查询。消息超过保守UTF8字节上限明确拒绝，不截断。
Accepted只代表受理；提交后的超时、取消、响应错误或5xx保持OutcomeUnknown，不自动重发或换通道。

协议参考：[钉钉接入](https://open.dingtalk.com/document/robots/custom-robot-access)、[钉钉加签](https://open.dingtalk.com/document/robots/customize-robot-security-settings)、[PushPlus消息接口](https://www.pushplus.plus/doc/guide/api.html)。
