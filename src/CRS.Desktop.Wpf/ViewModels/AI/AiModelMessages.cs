using CRS.AI.Models;

namespace CRS.DesktopClient.ViewModels.AI;

/// <summary>用固定中文反馈区分模型、凭证和本机工具故障。</summary>
internal static class AiModelMessages
{
    public static string Explain(AiModelException error) => error.Code switch
    {
        "ApiKeyMissing" => "请填写此供应商的 API Key。",
        "InvalidApiKey" => "API Key 格式无效，请重新粘贴完整密钥。",
        "ApiKeyUnreadable" or "ApiKeyProtectFailed" => "无法使用当前 Windows 用户的密钥，请重新配置。",
        "ApiUnauthorized" or "ApiForbidden" => "供应商拒绝了请求，请检查 API Key 和项目权限。",
        "ApiPaymentRequired" or "ApiRateLimited" => "请检查供应商的余额、额度或限流状态。",
        "ApiModelUnavailable" or "ApiRequestRejected" => "供应商拒绝此模型请求，请检查模型权限或换一个模型。",
        "ApiTimeout" or "ApiConnectionFailed" or "ApiUnavailable" => "无法连接供应商，请检查网络后重试。",
        "DefaultModelMissing" => "请先在「设置 → AI 模型」配置默认模型。",
        "DefaultModelChanged" => "默认模型已在另一窗口更改。请打开设置确认当前模型后重试。",
        "InvalidModel" => "请选择受支持的供应商和模型。",
        "SettingsReadFailed" => "无法读取本机 AI 模型配置，请检查文件和访问权限。",
        "SettingsWriteFailed" => "无法保存 AI 模型配置，请检查权限或关闭其他 CRS 窗口后重试。",
        "Unauthorized" => "当前 Windows 用户尚未获准使用 Gateway。",
        "GatewayUnavailable" => "无法连接 Gateway，请检查是否已安装并启动。",
        "Unsupported" or "UpstreamUnavailable" => "Gateway 暂时无法查询 Worker，请检查新版 Worker 及插件。",
        "AuditUnavailable" => "Gateway 审计暂时不可用，请检查服务。",
        "BudgetExceeded" => "本机查询暂时繁忙，请稍后重试。",
        "InvalidPrompt" => "请输入 1 至 4000 个字符的公开 ETF 问题。",
        "ImageUnsupported" => "当前模型不支持截图，请移除截图或更换模型。",
        "InvalidScreenshot" => "截图格式无效或过大，请重新选择截图。",
        "IncompleteResponse" or "TurnBudgetExceeded" => "请求达到处理限制，请缩短问题或拆开查询。",
        _ => "操作未完成，收到无法处理的结果。"
    };
}
