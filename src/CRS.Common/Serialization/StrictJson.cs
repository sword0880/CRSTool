using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace CRS.Serialization;

/// <summary>有界JSON入口，拒绝重复字段、未知字段及缺失的必填参数，不解释业务含义。</summary>
public static class StrictJson
{
    private static readonly JsonSerializerOptions Options=new(){MaxDepth=32,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static T Read<T>(string value,int maximumBytes)
    {
        if(maximumBytes is <1 or >8*1024*1024||Encoding.UTF8.GetByteCount(value)>maximumBytes)throw new InvalidDataException("JSON正文超过限额。");
        try
        {
            using var document=JsonDocument.Parse(value,new(){MaxDepth=32});Duplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(value,Options)??throw new InvalidDataException("JSON正文为空。");
        }
        catch(JsonException error){throw new InvalidDataException("JSON结构或字段不符。",error);}
    }
    private static void Duplicates(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw new InvalidDataException("JSON含重复字段。");Duplicates(property.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Duplicates(item);
    }
}
