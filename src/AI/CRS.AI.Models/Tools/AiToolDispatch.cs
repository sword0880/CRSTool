using System.Text.Json;
using CRS.AI.Contracts;

namespace CRS.AI.Models.Tools;

/// <summary>为 MCP 和 Responses API 统一检查工具名称及参数外层格式，再交由 Gateway 校验业务参数。</summary>
internal sealed class AiToolDispatch(Func<AiCall, CancellationToken, Task<AiResult>> query)
{
    public static JsonElement EmptyArguments { get; } = JsonSerializer.SerializeToElement(new { });

    public Task<AiResult> ExecuteAsync(string? name, JsonElement arguments, CancellationToken token)
    {
        // 模型和 MCP 客户端只能指定固定工具，调用者身份始终由 Windows 管道校验。
        if (name is not (AiCatalog.Overview or AiCatalog.Coverage))
            return Task.FromResult(AiResult.Reject("unlisted", "UnknownTool"));
        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 4096)
            return Task.FromResult(AiResult.Reject(name, "InvalidArguments"));
        return query(new AiCall(name, arguments), token);
    }
}
