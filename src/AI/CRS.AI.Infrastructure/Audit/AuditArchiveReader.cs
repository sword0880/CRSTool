using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.AI.Query;

namespace CRS.AI.Infrastructure;

internal static class AuditArchiveReader
{
    private static readonly JsonSerializerOptions Strict = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4
    };

    public static (int Events, string Sha256) Inspect(Stream source)
    {
        var block = new byte[64 * 1024];
        var line = new byte[AuditFileRules.MaximumEventBytes];
        var used = 0;
        var events = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var read = source.Read(block); read != 0; read = source.Read(block))
        {
            var bytes = block.AsSpan(0, read);
            hash.AppendData(bytes);
            while (!bytes.IsEmpty)
            {
                var newline = bytes.IndexOf((byte)'\n');
                var length = newline < 0 ? bytes.Length : newline;
                if (length > line.Length - used) throw new InvalidDataException("审计记录长度超限。");
                bytes[..length].CopyTo(line.AsSpan(used));
                used += length;
                if (newline < 0) break;
                var entry = JsonSerializer.Deserialize<AiReadAuditEvent>(line.AsSpan(0, used), Strict)
                    ?? throw new InvalidDataException("审计记录为空。");
                AuditFileRules.ValidateEvent(entry);
                events++;
                used = 0;
                bytes = bytes[(newline + 1)..];
            }
        }
        if (used != 0) throw new InvalidDataException("审计末行不完整。");
        return (events, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
