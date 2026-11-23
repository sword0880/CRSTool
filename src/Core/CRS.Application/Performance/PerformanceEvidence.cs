using System.Security.Cryptography;
using System.Text.Json;

namespace CRS.Application.Performance;
/// <summary>结果指纹绑定范围、版本与输入摘要；不声称无密钥摘要可以阻挡持主密码的恶意修改者。</summary>
public static class PerformanceEvidence
{
    public static string Fingerprint(PerformanceRun run)
    {
        // 空集合使用原版签名载荷，旧历史不得因新增元数据字段被判为篡改。
        object evidence=new{run.Id,run.ParentId,run.CreatedAt,run.InputHash,run.Result};
        if(!run.ImportProvenance.IsDefaultOrEmpty)
        {
            if(run.ImportProvenance.Length>500||run.ImportProvenance.Select(p=>p.SourceFileHash).Distinct().Count()!=run.ImportProvenance.Length)throw new CrsException("历史解析版本依据重复或超限。");
            foreach(var p in run.ImportProvenance)p.Validate();
            evidence=new{run.Id,run.ParentId,run.CreatedAt,run.InputHash,run.Result,run.ImportProvenance};
        }
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence)));
    }
    public static PerformanceRun Seal(PerformanceRun run)=>run with{ResultSeal=Fingerprint(run)};
    public static void Verify(PerformanceRun run)
    {if(run.ResultSeal.Length!=64 || run.ResultSeal!=Fingerprint(run))throw new CrsException("绩效结果指纹不一致，不能作为原始历史发布。");}
}
