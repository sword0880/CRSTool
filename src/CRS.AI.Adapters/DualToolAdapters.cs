using System.Text.Json;
using CRS.AI.Contracts;
namespace CRS.AI.Adapters;

// Protocol-adjacent wrappers only. No HTTP listener, MCP SDK, OpenAI API key or model invocation.
public sealed record McpToolDescription(string Name, string Description, JsonElement InputSchema);
public sealed record OpenAiFunctionDefinition(string Type, string Name, string Description,
    JsonElement Parameters, bool Strict);

public sealed class McpReadToolAdapter(IAiReadGateway gateway)
{
    public IReadOnlyList<McpToolDescription> ListTools() =>
        gateway.Tools.Select(x => new McpToolDescription(x.Name, x.Description, x.Schema)).ToArray();

    public Task<AiResult> CallToolAsync(AiCaller caller, AiCall call, CancellationToken token)
        => gateway.ExecuteAsync(caller, call, token);
}

public sealed class OpenAiFunctionReadAdapter(IAiReadGateway gateway)
{
    // Responses API function tools use type/name/description/parameters/strict.
    public IReadOnlyList<OpenAiFunctionDefinition> ListFunctions() =>
        gateway.Tools.Select(x => new OpenAiFunctionDefinition(
            "function", x.Name, x.Description, x.Schema, true)).ToArray();

    public Task<AiResult> ExecuteFunctionAsync(AiCaller caller, AiCall call, CancellationToken token)
        => gateway.ExecuteAsync(caller, call, token);
}
