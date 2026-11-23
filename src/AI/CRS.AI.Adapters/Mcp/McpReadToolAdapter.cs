using System.Text.Json;
using CRS.AI.Contracts;

namespace CRS.AI.Adapters;

/// <summary>MCP 工具列表项，包含工具名称、用途和输入参数结构。</summary>
public sealed record McpToolDescription(string Name, string Description, JsonElement InputSchema);

/// <summary>将共享只读工具目录转换为 MCP 描述，并将调用交给 Gateway。</summary>
public sealed class McpReadToolAdapter(IAiReadGateway gateway)
{
    public IReadOnlyList<McpToolDescription> ListTools() => DescribeTools(gateway.Tools);

    /// <summary>复用工具目录中的参数结构，使 MCP 公布的约定与 Gateway 校验一致。</summary>
    public static IReadOnlyList<McpToolDescription> DescribeTools(IEnumerable<AiTool> tools) =>
        tools.Select(tool => new McpToolDescription(tool.Name, tool.Description, tool.Schema)).ToArray();

    /// <summary>使用可信宿主确认的调用者身份执行只读查询。</summary>
    public Task<AiResult> CallToolAsync(AiCaller caller, AiCall call, CancellationToken token) =>
        gateway.ExecuteAsync(caller, call, token);
}
