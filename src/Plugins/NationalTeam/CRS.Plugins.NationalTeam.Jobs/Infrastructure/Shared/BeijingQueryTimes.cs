using System.Text.Json.Nodes;
using CRS.Timing;

namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>只归一公开查询副本中的时间元数据；不改写原件、已封存证据或交易日。</summary>
internal static class BeijingQueryTimes
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
        { "At", "CreatedAt", "CreatedUtc", "UpdatedAt", "FirstSeenAt", "FirstSeen", "AcceptedAt", "PublishedAt", "NextAttemptAt", "RetrievedAt", "RetrievedUtc", "Retrieved" };
    internal static string Normalize(string json)
    {
        var node = JsonNode.Parse(json);
        Visit(node);
        return node?.ToJsonString() ?? json;
    }
    private static void Visit(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj.ToArray())
            {
                if (Fields.Contains(name) && value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    obj[name] = BeijingClock.NormalizeText(text);
                else Visit(value);
            }
        }
        else if (node is JsonArray array) foreach (var value in array) Visit(value);
    }
}
