using System.Security.Principal;
using System.Text.Json;

namespace CRS.AI.Gateway.Authorization;

internal sealed class AuthorizationFile(string path)
{
    internal const int MaxSubjects = 128;
    private const int MaxBytes = 32 * 1024;

    public string[] Load()
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 2 or > MaxBytes) throw new InvalidDataException("授权文件长度超限。");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        var sids = JsonSerializer.Deserialize<string[]>(bytes);
        if (sids is null || sids.Length > MaxSubjects || sids.Any(sid => !IsValidSid(sid)) ||
            sids.Distinct(StringComparer.Ordinal).Count() != sids.Length)
            throw new InvalidDataException("授权文件格式不正确。");
        return sids;
    }

    public void Save(IEnumerable<string> sids)
    {
        // 在受保护目录中刷盘后原子替换。失败时旧文件继续有效，不报告授权变更成功。
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, sids.Order(StringComparer.Ordinal));
                output.Flush(flushToDisk: true);
            }
            File.Replace(temporary, path, destinationBackupFileName: null);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static bool IsValidSid(string? value)
    {
        if (value is not { Length: > 0 and <= 184 }) return false;
        try { return new SecurityIdentifier(value).Value == value; }
        catch (ArgumentException) { return false; }
    }
}
