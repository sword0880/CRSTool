using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;

internal static class AccountPeriodFileChecks
{
    /// <summary>凭证按同一读取内容计算摘要，拒绝重复确认、未知字段和未核对声明。</summary>
    internal static void Run(string root)
    {
        var directory=Path.Combine(root,"account-periods"); Directory.CreateDirectory(directory);
        var proof=Path.Combine(directory,"proof.txt"); var original="合成开户凭证：2025-06-17"u8.ToArray();
        File.WriteAllBytes(proof,original);
        var path=Path.Combine(directory,"periods.json");
        var json=JsonSerializer.Serialize(new {Version=1,Accounts=new[] {new
            {Account="A",Opened="2025-06-17",Closed=(string?)null,OpeningZeroConfirmed=true,ProofPath="proof.txt",Reviewed=true}}});
        File.WriteAllText(path,json); var evidence=AccountPeriodFiles.Read(path).Single();
        if(evidence.EvidenceSha256!=Convert.ToHexStringLower(SHA256.HashData(original)) || evidence.EvidenceName!="proof.txt"
            || evidence.Opened!=new DateOnly(2025,6,17) || !File.ReadAllBytes(proof).SequenceEqual(original))
            throw new Exception("期间凭证摘要或只读处理不正确。");
        Reject(json.Replace("\"Reviewed\":true","\"Reviewed\":false"));
        Reject(json.Replace("\"Reviewed\":true","\"Reviewed\":false,\"Reviewed\":true"));
        Reject(json.Replace("\"Reviewed\":true","\"Reviewed\":false,\"reviewed\":true"));
        Reject(json.Replace("\"Reviewed\":true","\"Reviewed\":true,\"Unknown\":1"));
        Reject(json.Replace("\"Opened\":\"2025-06-17\",",""));
        Reject(json.Replace("\"Version\":1","\"Version\":2"));
        File.WriteAllText(path,json); using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        try {AccountPeriodFiles.Read(path,cancellation.Token); throw new Exception("取消期间读取未生效。");}
        catch(OperationCanceledException) { }
        // 新元数据必须经加密存储往返，并在底稿中保留活动期及其验收边界。
        var store=TestStores.Create(Path.Combine(directory,"vault"));
        var result=new CalculationResult {Year=2025,SnapshotId="SYNTHETIC-PERIOD",Summary=new(0,0,0,0,0,0,0),
            AccountPeriods=[evidence],AccountCoverage=[new("A",evidence.Opened,new(2025,12,31),"AccountActivity",true)]};
        store.Save(result); var loaded=store.Load(result.SnapshotId);
        if(loaded.AccountPeriods?.Single()!=evidence || loaded.AccountCoverage?.Single().Kind!="AccountActivity")
            throw new Exception("期间依据未经过加密存储完整往返。");
        var report=Path.Combine(directory,"periods.xlsx"); ExcelReports.Export(report,loaded);
        var proofRows=CRS.Documents.Excel.ExcelReader.ReadSheet(report,"账户期间依据"); var coverageRows=CRS.Documents.Excel.ExcelReader.ReadSheet(report,"期间覆盖状态");
        if(proofRows[1][5]!=evidence.EvidenceSha256 || coverageRows[1][3]!="账户活动期" || !coverageRows[0][4].Contains("非税额签认"))
            throw new Exception("底稿遗漏期间依据、活动期类别或验收边界。");
        Console.WriteLine("期间凭证文件通过：相对路径、真实摘要、原件不变、必填／未知／重复字段、未核对及取消拒绝。");
        void Reject(string invalid)
        {File.WriteAllText(path,invalid); try {AccountPeriodFiles.Read(path);} catch(CrsException) {return;} throw new Exception("非法期间清单未拒绝。");}
    }
}
