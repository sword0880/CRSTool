# AI 助手附件处理

> 状态：附件模块源码、WPF 界面、MCP 协议和自动化测试已加入候选分支；构建/CI 及真实 ChatGPT 账号端到端验收待核验。

## 1. 目标与范围

WPF AI 助手、OpenAI API 模型调用和 MCP 附件入口共用 `CRS.AI.Models.Attachments` 的解析与策略：

- 用户选择文件、查看短预览、逐个移除。
- 问题和附件解析内容在同一条模型 user message 中发送。
- 当前 WPF 会话和 MCP 应用层会话保留解析文本，后续追问继续携带。
- 原始文件只在读取/下载和解析期间短暂驻留内存，不写入临时目录、SQLCipher 聊天历史或日志。
- 新对话、显式结束、锁定、退出及 MCP 会话超时清理附件解析缓冲。

首版格式白名单为 TXT、MD、CSV、JSON（UTF-8）。PDF、DOCX、XLSX 和图片 OCR 不在本次解析范围；需要分别完成压缩包、宏、外部链接、页数及资源上限审查后再加入。

## 2. 统一限制

| 项目 | 限制 |
| --- | ---: |
| 单文件原始大小 | 10 MiB |
| 会话文件总大小 | 20 MiB |
| 会话附件数量 | 5 |
| 会话提取文本总量 | 50,000 字符 |
| JSON 最大深度 | 32 |
| CSV 最大行数 | 10,000 |
| CSV 单行长度 | 16,384 字符 |
| 文件名长度 | 128 字符 |
| UI 预览 | 最多 240 字符 |

校验不信任扩展名、MIME 或客户端声明；三者必须符合白名单关系。拒绝无效 UTF-8、NUL/非预期控制字符、损坏 JSON、路径分隔符及超限文件。CSV/TXT/MD 只作为文本读取，不会执行公式、宏、脚本或外部链接。

## 3. 数据流与隐私

```text
WPF OpenFileDialog ─┐
                   ├─> AiAttachmentParser ─> AiAttachmentDocument（仅内存）
ChatGPT fileParams ─┘                             │
                                                 ├─> Responses API / Anthropic user message
                                                 └─> MCP 会话上下文 → ChatGPT tool result
```

- `AiAttachmentParser` 读取有界字节、验证并生成 SHA-256 与提取文本。
- 原始字节缓冲在本地读取或 MCP 下载完成后尽力清零。
- `AiAttachmentDocument` 用可清零字符数组保存解析文本；移除、结束和过期时尽力清零。
- 模型请求体在发送完成后尽力清零。
- CLR 字符串、JSON 序列化、HTTP 栈和模型 SDK 可能产生内部副本；不能承诺物理内存零残留。
- 聊天历史只保存用户问题、答案，以及本条消息的附件文件名提示；不保存原始文件字节或附件提取正文。
- 重新打开历史对话不会恢复附件正文；用户需要重新上传。
- 不记录原始文件名路径、下载 URL、附件正文或 API Key。

## 4. WPF 行为

- 附件按钮只允许 TXT、MD、CSV、JSON；可多选，最多 5 个。
- 添加后显示文件名、提取字符数和短预览。
- 每个附件有独立移除操作。
- 当前会话中的附件会在每轮追问中再次作为引用数据加入同一条 user message。
- 发送成功后，附件保留在当前会话；发送失败时保留，方便修改问题后重试。
- 新建对话、锁定、退出时清理附件。
- 恢复历史问答只恢复可见问答和用户选择保留的截图，不恢复附件。

## 5. MCP / ChatGPT 行为

MCP 协议仍使用 Stateless Streamable HTTP；业务附件会话不依赖 HTTP 连接或 MCP transport session，而由 CRS 签发高熵会话句柄管理。

| 工具 | 用途 |
| --- | --- |
| `crs_chat_start` | 创建临时附件会话 |
| `crs_chat_message` | 同一条消息接收问题与文件；后续追问复用同一个 sessionId |
| `crs_chat_remove_attachment` | 按 attachmentId 移除单个附件 |
| `crs_chat_end` | 结束会话并清理全部附件 |

`crs_chat_message` 声明 `_meta["openai/fileParams"] = ["files"]`，文件对象遵循 ChatGPT 文件参数结构：必需的 `download_url`、`file_id`，以及可选的 `mime_type`、`file_name`。CRS 下载器：

- 仅允许 HTTPS 和 OpenAI/ChatGPT 文件域名。
- 禁止 IP 字面量、非标准端口、用户信息、片段和重定向。
- 禁用代理与 Cookie；限制连接时间、总时间及 10 MiB 响应体。
- 不记录临时下载 URL；下载后立即走统一解析器，原始字节不落盘。
- MCP JSON 请求正文限制为 64 KiB；只传文件引用，不通过 MCP 正文传输 Base64 文件字节。

应用层会话使用 256-bit 随机 Base64URL 句柄；绑定当前 MCP Bearer 凭证 SHA-256 摘要。空闲 30 分钟或绝对寿命 2 小时过期；最多 64 个活动会话；后台每分钟清理过期内容。进程停止时清空所有会话。

**身份边界：**当前 MCP 配置仍使用安装级 `CRS_MCP_TOKEN`。使用相同令牌的多个 MCP 客户端共享同一个调用者身份域，但仍需持有不可猜测的 sessionId。若未来要支持多用户或跨组织部署，必须先增加每用户 OAuth/独立凭证与会话撤权；不能把安装级令牌当作多租户身份系统。

## 6. 提示注入边界

附件文本始终视为不可信引用数据：

- 模型系统指令明确要求忽略附件内要求改变系统规则、泄露资料、调用未授权工具或执行操作的指令。
- 附件正文使用 JSON 编码后作为用户消息中的资料区，不能伪造外围文本分隔符。
- 附件不授予额外 Gateway 工具权限；原有只读工具白名单、调用者授权和审计策略不变。
- 文件内容不能触发本地命令、SQL、脚本、宏或自动文件打开。

## 7. 验证

在 Windows / .NET 10 环境运行：

```powershell
dotnet build CRS.sln -c Release -m:1
dotnet run --project tests/CRS.Tests.AiQuery -c Release
dotnet run --project tests/CRS.Tests.AiQuery -c Release -- --client-integration
```

自动化检查覆盖：格式/MIME/UTF-8/JSON/控制字符/路径穿越/大小/数量、会话调用者隔离、逐个移除、TTL、显式结束、MCP fileParams 元数据、上传同条消息、连续追问、移除后的上下文清空及会话结束后句柄失效。

真实 ChatGPT 上传验收仍需在目标 ChatGPT 自定义 MCP 连接中验证：文件参数能否实际到达工具、临时下载域名是否在 OpenAI 当前环境下匹配、用户授权提示是否符合预期。自动化测试使用假下载器，不代表已完成真实 ChatGPT 账号端到端验收。

## 8. 开放项

1. 当前首版仅支持 UTF-8 文本类附件；PDF、DOCX、XLSX 应独立增加安全解析验收，不应仅凭现有业务解析器可用就直接开放。
2. 会话 TTL 是兜底清理，不等同于知道 ChatGPT 对话何时结束；客户端应在明确结束时调用 `crs_chat_end`。
3. 多用户部署前必须将共享 Bearer 凭证升级为用户级认证与撤权。
4. 真实 ChatGPT fileParams 下载域名若变化，需更新允许列表并回归 SSRF / 重定向测试，不得退化为任意 HTTPS URL。
