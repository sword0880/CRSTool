using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Infrastructure;

internal static class RealSampleAcceptance
{
    /// <summary>从用户本地脱敏原件验收人工期望值；只保存摘要和金额核对结果，不复制原始报表。</summary>
    public static void Run(string manifestPath,string output)
    {
        // 人工基准必须完整填写，不能让遗漏字段自动成为零值期望。
        var options=new JsonSerializerOptions { PropertyNameCaseInsensitive=true,RespectRequiredConstructorParameters=true,
            UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
        var manifest=JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestPath),options) ?? throw new Exception("真实验收清单为空。");
        if (manifest.SourceKind!="real-anonymized" || string.IsNullOrWhiteSpace(manifest.OwnerAttestation)
            || manifest.Broker is not ("IBKR" or "FUTU") || manifest.Files is null || manifest.Files.Length==0 || manifest.Expected is null
            || !manifest.ScopeConfirmed || (!manifest.OpeningZero && string.IsNullOrWhiteSpace(manifest.OpeningPath)))
            throw new Exception("真实验收需声明脱敏来源、样本授权、完整账户范围、期初依据及人工期望值。");
        var directory=Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string Resolve(string path)=>Path.GetFullPath(path,directory);
        var files=manifest.Files.Select(Resolve).ToArray();
        var result=CalculationServices.Create(new ExchangeRates(Resolve(manifest.ConfigDirectory)),manifest.Broker,manifest.Year)
            .Calculate(files,manifest.Year,manifest.OpeningZero,manifest.ScopeConfirmed,
                string.IsNullOrWhiteSpace(manifest.OpeningPath)?null:Resolve(manifest.OpeningPath));
        if (result.Issues.Any(i=>i.Code=="INCOMPLETE_PERIOD")) throw new Exception("真实样本未覆盖全年。");
        var actual=new Expected(result.ImportedTradeCount??-1,result.Matches.Count,result.CoveredAccounts.Count,
            result.Summary.DividendCny,result.Summary.InterestCny,result.Summary.GainCny,result.Summary.SupplementTax,
            result.CashChecks.Count,result.PnlChecks.Count,result.ReconciliationStatus.ToString(),result.Issues.Select(i=>i.Code).Distinct().Order().ToArray());
        var expected=manifest.Expected with { IssueCodes=manifest.Expected.IssueCodes.Distinct().Order().ToArray() };
        if (JsonSerializer.Serialize(actual)!=JsonSerializer.Serialize(expected)) throw new Exception("真实样本与人工核对结果不一致；请独立核对期望值，禁止以程序结果替换人工基准。");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"real-sample.json"),JsonSerializer.Serialize(new
        {
            sourceKind=manifest.SourceKind,provenance="owner-attested; source authenticity not automatically verified",
            manifest.Year,manifest.Broker,createdUtc=DateTimeOffset.UtcNow, actual,
            originals=files.Select(path=>new { name=Path.GetFileName(path),sha256=Hash(path) }),
            openingSha256=string.IsNullOrWhiteSpace(manifest.OpeningPath)?null:Hash(Resolve(manifest.OpeningPath))
        },new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine("真实全年样本金额、账户数量和对账状态与人工期望值一致；来源真实性仍由样本提供方核实。");
        static string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    }
    private sealed record Manifest(string SourceKind,string OwnerAttestation,int Year,string Broker,string[] Files,
        string ConfigDirectory,bool OpeningZero,bool ScopeConfirmed,string? OpeningPath,Expected Expected);
    private sealed record Expected(int TradeCount,int MatchCount,int AccountCount,decimal DividendCny,decimal InterestCny,
        decimal GainCny,decimal SupplementTax,int CashCheckCount,int PnlCheckCount,string ReconciliationStatus,string[] IssueCodes);
}
