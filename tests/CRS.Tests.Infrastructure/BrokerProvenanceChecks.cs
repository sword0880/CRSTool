using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Application;
using CRS.Application.Performance;
using CRS.Domain;
using CRS.Domain.Performance;
using CRS.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;

internal static class BrokerProvenanceChecks
{
    internal static async Task RunAsync(string root)
    {
        const string password="Synthetic-Provenance-Password-2026";
        var passed=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS 解析版本持久化："+name);}
        using var vault=new VaultManager();var created=await vault.CreateAsync(root,password);await TestStores.Enroll(vault);
        var cases=vault.CreatePerformanceUseCases(false);
        var xml=Path.Combine("tests","fixtures","ibkr_activity.xml");
        var xlsx=Path.Combine("tests","fixtures","futu_performance.xlsx");
        Check(await cases.ImportAsync(xml,"IBKR"),"IBKR新导入成功");
        var workspace=await cases.LoadAsync();var p=workspace.Sources.Single().Provenance!;
        Check(p is {Version:1,Broker:"IBKR",ReaderVersion:"IBKR.SourceFacts.v1",ProjectionVersion:"IBKR.Performance.v1"},"实际读取/投影版本已保存");
        Check(p.SourceFileHash==FrozenInput.Read(xml).Sha256&&p.DataSnapshotId==PerformanceSnapshot.Identity(workspace.Data),"版本绑定原件、批次及导入事实摘要");
        var request=new PerformanceRequest(workspace.Data.Accounts.Select(a=>a.Id).ToArray(),new(2025,1,1),new(2025,12,31),"USD");
        var run=await cases.CalculateAsync(request);
        Check(run.ImportProvenance.Single()==p,"新运行冻结同一份版本依据");
        var manifest=PerformanceRunManifests.From(run);
        Check(manifest.ImportProvenance.Single()==p&&manifest.NormalizationVersion.StartsWith("Recorded",StringComparison.Ordinal),"运行清单提供逐原件版本依据");
        var corrupt=run with{ImportProvenance=[p with{ReaderVersion="other.v1"}]};
        try{PerformanceEvidence.Verify(corrupt);throw new Exception("版本篡改未拒绝");}catch(CrsException){Check(true,"结果指纹覆盖解析版本，拒绝篡改");}
        try{PerformanceEvidence.Verify(run with{ImportProvenance=[]});throw new Exception("版本剥除未拒绝");}catch(CrsException){Check(true,"剥除新历史版本字段不能伪装旧历史");}
        using(var c=vault.OpenForTest())
        {
            c.Execute("CREATE TRIGGER fail_provenance BEFORE UPDATE ON performance_settings BEGIN SELECT RAISE(ABORT,'injected'); END");
            try{await cases.ImportAsync(xlsx,"FUTU");throw new Exception("写故障意外成功");}catch(SqliteException){Check(true,"版本写入故障向上报告");}
            Check(c.ExecuteScalar<int>("SELECT COUNT(*) FROM performance_documents")==1,"原件与版本事务一起回滚，没有半份导入");
            c.Execute("DROP TRIGGER fail_provenance");
        }
        Check(await cases.ImportAsync(xlsx,"FUTU"),"故障恢复后富途导入成功");
        Check((await cases.LoadAsync()).Sources.Single(s=>s.Provenance?.Broker=="FUTU").Provenance is {ReaderVersion:"FutuAnnualActivity.v1",ProjectionVersion:"FUTU.Performance.v1"},"富途记录实际共享事实与投影版本");
        var beforeSettings=Array.Empty<byte>();
        using(var c=vault.OpenForTest())
        {
            beforeSettings=c.ExecuteScalar<byte[]>("SELECT payload FROM performance_settings WHERE id=1")!;
            Check(JsonNode.Parse(beforeSettings)!["Version"]!.GetValue<int>()==2,"新写入明确使用设置格式2，不伪装旧格式");
            var node=JsonNode.Parse(beforeSettings)!;
            node["ImportProvenance"]![p.SourceFileHash]!["DataSnapshotId"]=new string('0',64);
            c.Execute("UPDATE performance_settings SET payload=@payload WHERE id=1",new{payload=Encoding.UTF8.GetBytes(node.ToJsonString())});
        }
        try{await cases.LoadAsync();throw new Exception("版本/数据摘要不匹配未拒绝");}catch(CrsException){Check(true,"来源版本与保存的事实摘要不匹配时失败关闭");}
        using(var c=vault.OpenForTest())c.Execute("UPDATE performance_settings SET payload=@payload WHERE id=1",new{payload=beforeSettings});
        var replay=await cases.ReplayAsync(run.Id);
        await ProjectionComparisonChecks.RunAsync(cases,run,replay);
        Check(replay.ImportProvenance.Single()==p&&JsonSerializer.Serialize(replay.Result)==JsonSerializer.Serialize(run.Result),"重放保留版本且金额/结果不变");
        var backup=root+".crsbak";await vault.BackupAsync(backup);
        using(var restored=new VaultManager())
        {
            var target=root+"-restored";await restored.RestoreAsync(backup,target,created.RecoveryKey,true,password);await restored.UnlockAsync(target,password);await TestStores.Authenticate(restored);
            var recovered=restored.CreatePerformanceUseCases(false);
            Check((await recovered.LoadAsync()).Sources.Any(s=>s.Provenance==p)&&(await recovered.OpenRunAsync(run.Id)).ImportProvenance.Single()==p,"解析版本随加密备份、来源与历史完整恢复");
        }
        // 模拟真实旧格式：只移除新增字段，不重算或改变旧冻结输入/结果。
        using(var c=vault.OpenForTest())
        {
            var old=JsonNode.Parse(beforeSettings)!;old.AsObject().Remove("ImportProvenance");old["Version"]=1;
            c.Execute("UPDATE performance_settings SET payload=@payload WHERE id=1",new{payload=Encoding.UTF8.GetBytes(old.ToJsonString())});
        }
        Check((await cases.LoadAsync()).Sources.All(s=>s.Provenance is null),"旧设置缺字段仍能读取，并保留未知状态");
        Check(!await cases.ImportAsync(xml,"IBKR")&&(await cases.LoadAsync()).Sources.All(s=>s.Provenance is null),"重复导入不以当前版本补填旧记录");
        Check((await cases.OpenRunAsync(run.Id)).ImportProvenance.Single()==p,"当前来源元数据变动不回写历史");
        var legacy=run with{ImportProvenance=[]};
        var legacySeal=Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{legacy.Id,legacy.ParentId,legacy.CreatedAt,legacy.InputHash,legacy.Result})));
        var legacyNode=JsonNode.Parse(PerformanceCodec.Encode(run))!;legacyNode.AsObject().Remove("ImportProvenance");legacyNode["ResultSeal"]=legacySeal;
        using(var c=vault.OpenForTest())c.Execute("UPDATE performance_runs SET payload=@payload WHERE id=@Id",new{run.Id,payload=Encoding.UTF8.GetBytes(legacyNode.ToJsonString())});
        var oldRun=await cases.OpenRunAsync(run.Id);
        Check(oldRun.ImportProvenance.IsEmpty&&PerformanceRunManifests.From(oldRun).NormalizationVersion.StartsWith("Unrecorded",StringComparison.Ordinal),"旧历史的原签名载荷兼容，版本仍未记录");
        Check((await cases.ReplayAsync(run.Id)).ImportProvenance.IsEmpty,"旧历史重放不冒用当前读取版本");
        vault.Lock();await vault.BeginLocalUnlockAsync(root);await TestStores.Authenticate(vault);
        Check((await vault.CreatePerformanceUseCases(false).LoadAsync()).Sources.All(s=>s.Provenance is null),"真实关闭/重开库后旧格式仍兼容");
        Console.WriteLine($"解析版本持久化通过：{passed}项。");
    }
}
