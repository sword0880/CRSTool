using System.Security.Cryptography;
using System.Text.Json;

namespace CRS.Application.Performance;
/// <summary>结果指纹绑定范围、版本与输入摘要；不声称无密钥摘要可以阻挡持主密码的恶意修改者。</summary>
public static class PerformanceEvidence
{
    public static string Fingerprint(PerformanceRun run)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {run.Id,run.ParentId,run.CreatedAt,run.InputHash,run.Result})));
    public static PerformanceRun Seal(PerformanceRun run)=>run with{ResultSeal=Fingerprint(run)};
    public static void Verify(PerformanceRun run)
    {if(run.ResultSeal.Length!=64 || run.ResultSeal!=Fingerprint(run))throw new CrsException("绩效结果指纹不一致，不能作为原始历史发布。");}
}
