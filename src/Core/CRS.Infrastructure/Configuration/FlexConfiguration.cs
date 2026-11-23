using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Domain;

namespace CRS.Infrastructure;

public static class FlexConfiguration
{
    /// <summary>读取用户配置的年度查询 ID，明确禁止在配置文件中保存服务令牌。</summary>
    public static string ReadQueryId(string directory)
    {
        var file = Path.Combine(directory, "ibkr_flex.json"); if (!File.Exists(file)) return "";
        using var document = JsonDocument.Parse(File.ReadAllBytes(file));
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any(p => p.Name.Contains("token", StringComparison.OrdinalIgnoreCase)))
            throw new CrsException("Flex 配置必须是对象，且不能保存服务令牌。");
        if (!document.RootElement.TryGetProperty("annual_query_id", out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) throw new CrsException("Flex 查询 ID 配置须为字符串。");
        var id = value.GetString()?.Trim() ?? "";
        if (id != "" && !Regex.IsMatch(id, "^[0-9]+$")) throw new CrsException("Flex 查询 ID 配置须为数字。");
        return id;
    }
}
