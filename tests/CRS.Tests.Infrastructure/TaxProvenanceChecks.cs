using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;
using NPOI.XSSF.UserModel;

internal static class TaxProvenanceChecks
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var passed=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS 税务版本："+label);}
        void Reject(Action work,string label)
        {try{work();throw new Exception("意外允许");}catch(CrsException){Check(true,label);}}
        var source=Path.Combine(root,"activity.xml");File.Copy("tests/fixtures/ibkr_activity.xml",source);
        var service=CalculationServices.Create(new Rates());
        var result=service.Calculate([source],2025,true,true,saveCanonicalSnapshot:true);
        var provenance=result.ImportProvenance!.Single();
        Check(provenance.ReaderVersion==IbkrSourceReader.Version&&provenance.ProjectionVersion==IbkrXmlParser.ProjectionVersion,"保存实际共享读取和税务投影版本");
        Check(provenance.SourceFileHash==result.Sources.Single().Sha256&&provenance.ImportBatchId==provenance.SourceFileHash,"版本绑定原件与批次");
        var opening=Path.Combine(root,"opening.xml");
        File.WriteAllText(opening,"<FlexQueryResponse><FlexStatements><FlexStatement accountId=\"UTEST001\" fromDate=\"20240101\" toDate=\"20241231\"><Trades/><CashTransactions/><OpenPositions/></FlexStatement></FlexStatements></FlexQueryResponse>");
        var imported=new IbkrXmlParser().Parse([source],opening);
        Check(imported.ImportProvenance is{Count:2}&&imported.Sources.Count==2,"年度报告与期初报告各自记录实际读取版本");
        using var vault=new VaultManager();await vault.CreateAsync(Path.Combine(root,"vault"),"Synthetic-Tax-Provenance-2026");await TestStores.Enroll(vault);
        vault.Repository.Save(result);
        var loaded=vault.Repository.Load(result.SnapshotId);
        Check(loaded.ImportProvenance!.Single()==provenance,"版本与计算结果持久化后相同");
        Reject(()=>loaded.ImportProvenance!.Add(provenance),"载入后版本集合冻结");
        File.Delete(source);
        var replay=service.Replay(loaded);
        Check(replay.ImportProvenance!.SequenceEqual(loaded.ImportProvenance!)&&replay.Summary==loaded.Summary,"删除原件后重放沿用历史版本与原金额");
        Check(replay.CanonicalInputDigest==loaded.CanonicalInputDigest,"版本审计不改变规范输入契约或重放摘要");
        var node=JsonSerializer.SerializeToNode(loaded)!.AsObject();
        node[nameof(CalculationResult.ImportProvenance)]![0]![nameof(TaxImportProvenance.ProjectionVersion)]="IBKR.Tax.changed";
        Reject(()=>RunEvidenceSeal.Verify(node.Deserialize<CalculationResult>()!),"修改版本破坏运行封存");
        node=JsonSerializer.SerializeToNode(loaded)!.AsObject();node.Remove(nameof(CalculationResult.ImportProvenance));
        Reject(()=>RunEvidenceSeal.Verify(node.Deserialize<CalculationResult>()!),"剥除新版本依据破坏运行封存");
        node=JsonSerializer.SerializeToNode(loaded)!.AsObject();node.Remove(nameof(CalculationResult.EvidenceSeal));
        node[nameof(CalculationResult.ImportProvenance)]![0]![nameof(TaxImportProvenance.SourceFileHash)]=new string('a',64);
        Reject(()=>node.Deserialize<CalculationResult>()!.Freeze(),"即使未封存也拒绝不匹配的原件依据");
        node=JsonSerializer.SerializeToNode(loaded)!.AsObject();node.Remove(nameof(CalculationResult.EvidenceSeal));
        node[nameof(CalculationResult.ImportProvenance)]![0]![nameof(TaxImportProvenance.Version)]=2;
        Reject(()=>node.Deserialize<CalculationResult>()!.Freeze(),"未知版本依据格式拒绝");
        var legacyNode=JsonSerializer.SerializeToNode(loaded)!.AsObject();legacyNode.Remove(nameof(CalculationResult.ImportProvenance));legacyNode.Remove(nameof(CalculationResult.EvidenceSeal));
        var legacy=legacyNode.Deserialize<CalculationResult>()!;legacy.SnapshotId="TAX-LEGACY-UNKNOWN";
        vault.Repository.Save(legacy);
        var old=vault.Repository.Load(legacy.SnapshotId);
        Check(old.ImportProvenance is null&&!JsonSerializer.SerializeToNode(old)!.AsObject().ContainsKey(nameof(CalculationResult.ImportProvenance)),"缺版本旧形态保持未知且不写入空字段");
        RunEvidenceSeal.Verify(old);
        Check(service.Replay(old).ImportProvenance is null,"旧形态重放不以当前版本回填");
        var export=Path.Combine(root,"report.xlsx");new ExcelReportExporter().Export(export,loaded);
        using(var stream=File.OpenRead(export))using(var workbook=new XSSFWorkbook(stream))
            Check(workbook.GetSheet("解析版本").GetRow(1).GetCell(4).StringCellValue==provenance.ProjectionVersion,"底稿列出实际税务投影版本");
        var unknownSource=loaded.Sources.Single() with{File="unknown.xml",Sha256=new string('b',64),Account="OTHER"};
        var knownPart=new CalculationResult{Year=2025,Summary=new(0,0,0,0,0,0,0),SnapshotId="known-part",Sources=loaded.Sources.ToList(),ImportProvenance=[provenance],CoveredAccounts=["UTEST001"]};
        var unknownPart=new CalculationResult{Year=2025,Summary=new(0,0,0,0,0,0,0),SnapshotId="unknown-part",Sources=[unknownSource],CoveredAccounts=["OTHER"]};
        var aggregate=new AnnualTaxAggregationService().Aggregate("SYNTHETIC",2025,[knownPart,unknownPart]);
        Check(aggregate.ImportProvenance!.Single()==provenance&&aggregate.Sources.Count==2,"年度汇总保留已知依据且不为未知来源补造版本");
        var backup=Path.Combine(root,"tax.crsbak");await vault.BackupAsync(backup);vault.Lock();
        using var restored=new VaultManager();var target=Path.Combine(root,"restored");
        await restored.RestoreAsync(backup,target,"Synthetic-Tax-Provenance-2026",false,"Restored-Tax-Provenance-2026");
        await restored.UnlockAsync(target,"Restored-Tax-Provenance-2026");await TestStores.Authenticate(restored);
        Check(restored.Repository.Load(result.SnapshotId).ImportProvenance!.Single()==provenance,"加密备份恢复保留税务版本");
        Console.WriteLine($"税务版本依据通过：{passed}项。");
    }
    private sealed class Rates:IExchangeRateProvider
    {
        public string Fingerprint=>"synthetic-tax-provenance";
        public AppliedRate Get(int year,string currency)=>new(year,currency,1,"synthetic","","",null,null,false,true);
    }
}
