using System.Text;
using System.Text.Json;

namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>插件业务版本独立于Host IPC和数据库Schema。缺版本仅代表已发布的V1，未知版本不降级。</summary>
public static class EtfBusinessProtocol
{
    public const int Version=1;
    public const int MaximumRequestBytes=4096;
    private static readonly JsonSerializerOptions Json=new(){MaxDepth=32,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true};

    public static EtfRequest ParseRequest(string payload)
    {
        if(Encoding.UTF8.GetByteCount(payload)>MaximumRequestBytes)throw new EtfContractException("PayloadTooLarge");
        try
        {
            using var document=JsonDocument.Parse(payload,new(){MaxDepth=32});
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)throw new EtfContractException("InvalidRequest");
            RejectDuplicates(root);ValidateVersion(root);
            var request=JsonSerializer.Deserialize<EtfRequest>(payload,Json)??throw new EtfContractException("InvalidRequest");
            if(string.IsNullOrWhiteSpace(request.Operation)||request.Operation.Length>64)throw new EtfContractException("InvalidRequest");
            return request;
        }
        catch(JsonException){throw new EtfContractException("InvalidRequest");}
    }

    public static T ReadResponse<T>(string payload)
    {
        try
        {
            using var document=JsonDocument.Parse(payload,new(){MaxDepth=32});
            var root=document.RootElement;RejectDuplicates(root);
            if(root.ValueKind==JsonValueKind.Object)
            {
                if(root.TryGetProperty("ContractError",out var failure))
                    throw new EtfContractException(failure.ValueKind==JsonValueKind.String?failure.GetString()??"InvalidResponse":"InvalidResponse");
                ValidateVersion(root);
            }
            return JsonSerializer.Deserialize<T>(payload,Json)??throw new EtfContractException("InvalidResponse");
        }
        catch(JsonException){throw new EtfContractException("InvalidResponse");}
    }

    private static void ValidateVersion(JsonElement root)
    {
        var number=1; // 缺版本永远只代表历史V1，不能随未来Current版本升级而变化。
        if(root.TryGetProperty("ContractVersion",out var version) &&
            (version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out number)))throw new EtfContractException("InvalidContractVersion");
        if(number!=Version)throw new EtfContractException("UnsupportedContractVersion");
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in value.EnumerateObject())
            {
                if(!names.Add(property.Name))throw new EtfContractException("DuplicateProperty");
                RejectDuplicates(property.Value);
            }
        }
        else if(value.ValueKind==JsonValueKind.Array)
            foreach(var item in value.EnumerateArray())RejectDuplicates(item);
    }
}

public sealed class EtfContractException(string code):Exception(code)
{
    public string Code{get;}=code;
}
public sealed record EtfContractFailure(string ContractError,int SupportedContractVersion=EtfBusinessProtocol.Version);
