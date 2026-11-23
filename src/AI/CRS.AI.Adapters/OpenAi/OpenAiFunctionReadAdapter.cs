using System.Text.Json;
using CRS.AI.Contracts;

namespace CRS.AI.Adapters;

/// <summary>提供给 OpenAI 的函数定义，包含参数结构和严格校验标记。</summary>
public sealed record OpenAiFunctionDefinition(string Type, string Name, string Description,
    JsonElement Parameters, bool Strict);

/// <summary>将共享只读工具目录转换为 OpenAI 函数定义，并将调用交给 Gateway。</summary>
public sealed class OpenAiFunctionReadAdapter(IAiReadGateway gateway)
{
    public IReadOnlyList<OpenAiFunctionDefinition> ListFunctions() => DescribeTools(gateway.Tools);

    /// <summary>为 Responses API 和离线验收生成相同的严格函数定义。</summary>
    public static IReadOnlyList<OpenAiFunctionDefinition> DescribeTools(IEnumerable<AiTool> tools) =>
        tools.Select(tool => new OpenAiFunctionDefinition(
            "function", tool.Name, tool.Description, tool.Schema, true)).ToArray();

    /// <summary>使用可信宿主确认的调用者身份执行模型请求的函数。</summary>
    public Task<AiResult> ExecuteFunctionAsync(AiCaller caller, AiCall call, CancellationToken token) =>
        gateway.ExecuteAsync(caller, call, token);
}
