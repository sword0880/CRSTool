using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.AI.Contracts;

namespace CRS.AI.Gateway.Transport;

internal static class LocalGatewayProtocol
{
    public const string PipeName = "CRS.AI.Gateway.v1";
    public const string ControlSuffix = ".control";
    public const int Version = 1;
    public const int MaxRequestBytes = 8192;
    public const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LocalReadRequest(int Version, Guid RequestId, string Tool, JsonElement Arguments);
internal sealed record LocalReadResponse(int Version, Guid RequestId, AiResult Result);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LocalAccessRequest(int Version, Guid RequestId, string Action, string? TargetSid = null);
internal sealed record LocalAccessResponse(int Version, Guid RequestId, string Code);
