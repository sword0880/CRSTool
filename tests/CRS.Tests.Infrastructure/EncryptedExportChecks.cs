using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;
using CRS.Security.Cryptography;

internal static class EncryptedExportChecks
{
    /// <summary>隔离资料验证密文隐私、可用结转、篡改／路径拒绝、取消及真实锁定提交门槛。</summary>
    public static async Task RunAsync(string root)
    {
        var directory=Path.Combine(root,"encrypted-exports"); Directory.CreateDirectory(directory);
        const string password="Synthetic-Export-Password-2026",marker="PRIVATE-ACCOUNT-SENTINEL";
        var result=new CalculationResult {Year=2025,SnapshotId="EXPORT",Summary=new(0,0,0,0,0,0,0),CarryEligible=true,
            CoveredAccounts=[marker],Sources=[new("private-input.xml",new string('a',64),marker,new(2025,1,1),new(2025,12,31),true,true,true)]};
        result.SealForPersistence("synthetic"); result.Freeze();
        var target=Path.Combine(directory,"protected.crsexport"); EncryptedReportFiles.Export(target,result,password);
        var text=File.ReadAllText(target); if(text.Contains(marker) || text.Contains(password) || text.Contains("report.xlsx") || text.Contains("\"Year\"")) throw new Exception("密文外层泄露资料。");
        var restored=EncryptedReportFiles.Restore(target,directory,password);
        if(CRS.Documents.Excel.ExcelReader.ReadSheet(Path.Combine(restored,"report.xlsx"),"计算说明").Count==0
            || CarryFiles.Read(Path.Combine(restored,"carry.json")).SourceSnapshotId!=result.SnapshotId) throw new Exception("恢复工作簿或结转不一致。");
        var existing=File.ReadAllBytes(target); var folders=Directory.GetDirectories(directory).Length;
        Reject<CrsException>(()=>EncryptedReportFiles.Restore(target,directory,"Incorrect-Export-Password")); Clean();
        var node=JsonNode.Parse(text)!.AsObject(); var tag=Convert.FromBase64String(node["Payload"]!["Body"]!["Tag"]!.GetValue<string>()); tag[0]^=1;
        node["Payload"]!["Body"]!["Tag"]=Convert.ToBase64String(tag);
        var damaged=Path.Combine(directory,"damaged.crsexport"); File.WriteAllText(damaged,node.ToJsonString());
        Reject<CrsException>(()=>EncryptedReportFiles.Restore(damaged,directory,password)); Clean();
        // 认证正确但正文包含路径或重复文件名，也必须先拒绝而非开始解包。
        void InvalidPackage(string[] names)
        {
            var plain=JsonSerializer.SerializeToUtf8Bytes(new {Version=1,Files=names.Select(name=>new {Name=name,Content=new byte[]{1}})});
            File.WriteAllBytes(damaged,JsonSerializer.SerializeToUtf8Bytes(new {Format="CRS.EncryptedExport.v1",Payload=PasswordEncryption.Seal(plain,password)}));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
            Reject<CrsException>(()=>EncryptedReportFiles.Restore(damaged,directory,password)); Clean();
        }
        InvalidPackage(["../outside.xlsx","evidence.json"]); InvalidPackage(["report.xlsx","report.xlsx","evidence.json"]);
        using var pre=new CancellationTokenSource(); pre.Cancel();
        Reject<OperationCanceledException>(()=>EncryptedReportFiles.Export(target,result,password,cancellation:pre.Token));
        Reject<CrsException>(()=>EncryptedReportFiles.Export(target,result,"short")); Unchanged();
        // 全部恢复文件写完后取消：暂存明文要清理，不能留部分输出。
        using var final=new CancellationTokenSource(); var calls=0;
        Reject<OperationCanceledException>(()=>EncryptedReportFiles.Restore(target,directory,password,final.Token,action=> {if(++calls==2) final.Cancel(); action();})); Clean();
        // 真正关闭保险库会话后，旧导出不得提交或覆盖已经存在的密文。
        using var vault=new VaultManager(); await vault.CreateAsync(Path.Combine(root,"encrypted-export-vault"),"Synthetic-Vault-Password");
        await TestStores.Enroll(vault); var guard=vault.CaptureGuard(); calls=0;
        // 第三次守卫才是文件写入完成后的最终提交，确保临时密文清理分支也被覆盖。
        Reject<CrsException>(()=>EncryptedReportFiles.Export(target,result,password,guard:action=> {if(++calls==3) vault.Lock(); guard(action);})); Unchanged(); Clean();
        Console.WriteLine("加密导出通过：密文隐私、Excel／结转恢复、错误密码／篡改／路径／重复拒绝、预取消、恢复提交取消及真实锁定保留原文件。");
        void Unchanged() {if(!File.ReadAllBytes(target).SequenceEqual(existing)) throw new Exception("失败覆盖了既有密文。");}
        void Clean() {if(Directory.GetDirectories(directory).Length!=folders || Directory.GetFiles(directory,"*.tmp").Length!=0) throw new Exception("失败留下明文暂存或新目录。");}
    }
    private static void Reject<T>(Action action) where T:Exception {try {action();} catch(T) {return;} throw new Exception("加密导出未拒绝："+typeof(T).Name);}
}
