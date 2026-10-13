using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;
using NLog;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

internal static class SensitiveExportChecks
{
    /// <summary>真实文件检查临时目录拒绝、XLSX 文本类型、加密恢复授权和日志允许字段。</summary>
    internal static async Task RunAsync(string root)
    {
        var directory=Path.Combine(root,"sensitive-exports"); Directory.CreateDirectory(directory);
        var attacks=new[] {"=1+2","+SUM(1,2)","-1+2","@SUM(1,2)","\t=1+2","\r=1+2","\n=1+2","＝1+2","＋1+2","－1+2","＠SUM(1,2)","=1+2\";,=3+4"};
        var result=new CalculationResult {Year=2025,SnapshotId="SYNTHETIC-EXPORT",Summary=new(0,0,-12.34m,0,0,0,0),Warnings=attacks.ToList(),CarryEligible=true,
            Sources=[new("synthetic.xml",new string('a',64),"SYNTHETIC-ACCOUNT",new(2025,1,1),new(2025,12,31),true,true,true)]};
        var store=TestStores.Create(Path.Combine(directory,"vault")); store.Save(result);
        var operations=new DesktopOperations(store,directory); var workflow=new DesktopWorkflow(operations);
        var plain=Path.Combine(directory,"ordinary.xlsx");
        await Reject(workflow.ExportAsync(plain,result,""));
        // 隔离验收本身位于 TEMP；普通授权仍不能自动授权临时目录。
        await Reject(workflow.ExportAsync(plain,result,"",consent:new(true)));
        if(File.Exists(plain) || Directory.GetFiles(directory,"*.tmp").Length!=0) throw new Exception("拒绝导出仍生成明文文件。");
        await workflow.ExportAsync(plain,result,"",consent:new(true,true));
        using(var input=File.OpenRead(plain)) using(var workbook=new XSSFWorkbook(input))
        {
            var warnings=workbook.GetSheet("提示");
            for(var i=0;i<attacks.Length;i++)
            {
                var cell=warnings.GetRow(i+1).GetCell(0);
                // XML 读取会把回车规范化为换行；不添加前缀，也不能将内容变成公式。
                var expected=attacks[i].Replace("\r\n","\n").Replace("\r","\n");
                if(cell.CellType!=CellType.String || cell.StringCellValue!=expected) throw new Exception($"合成恶意文本 {i} 的类型或内容不一致。");
            }
            for(var i=0;i<workbook.NumberOfSheets;i++)
                foreach(IRow row in workbook.GetSheetAt(i)) foreach(var cell in row.Cells)
                    if(cell.CellType==CellType.Formula) throw new Exception("底稿产生了公式单元格。");
            var gain=workbook.GetSheet("税务汇总").GetRow(3).GetCell(1);
            if(gain.CellType!=CellType.Numeric || gain.NumericCellValue!=-12.34) throw new Exception("公式防护改变了真实负金额。");
        }
        const string password="Synthetic-Archive-Password";
        var archive=Path.Combine(directory,"protected.crsexport"); await workflow.ExportEncryptedAsync(archive,result,"",password);
        await Reject(workflow.RestoreEncryptedAsync(archive,directory,password));
        await Reject(workflow.RestoreEncryptedAsync(archive,directory,password,consent:new(true)));
        var restored=await workflow.RestoreEncryptedAsync(archive,directory,password,consent:new(true,true));
        if(!File.Exists(Path.Combine(restored,"report.xlsx"))) throw new Exception("明确授权的合成恢复失败。");
        // 正规目录的加密归档允许 TEMP，普通明文的路径大小写、扩展前缀和点段均不能绕过。
        var temp=Path.GetFullPath(Path.GetTempPath());
        // 只读验证普通可信目录及相似前缀，不在生产输出目录创建测试文件。
        ExportDestinationPolicy.Validate(Path.Combine(AppContext.BaseDirectory,"never-written.xlsx"),true,false);
        var sibling=Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(temp))!,Path.GetFileName(Path.TrimEndingDirectorySeparator(temp))+"-other","never-written.xlsx");
        ExportDestinationPolicy.Validate(sibling,true,false);
        RejectPath(Path.Combine(temp.ToUpperInvariant(),"blocked.xlsx"));
        RejectPath(Path.Combine(temp,"sub","..","blocked.xlsx"));
        if(OperatingSystem.IsWindows()) RejectPath(@"\\?\"+Path.Combine(temp,"blocked.xlsx"));
        var previous=LogManager.Configuration;
        try
        {
            LocalStore.ConfigureLogging(directory,exactLogDirectory:true);
            operations.LogFailure("PRIVATE-ACCOUNT\r\nPassword=PRIVATE-PASSWORD DEK=PRIVATE-KEY");
            operations.LogExportEvent(SensitiveExportOperation.EncryptedArchive,SensitiveExportOutcome.Completed);
            LogManager.Flush(); LogManager.Configuration=previous;
            var log=File.ReadAllText(Path.Combine(directory,"crs.log"));
            if(log.Contains("PRIVATE-") || log.Contains(password) || log.Contains(result.SnapshotId) || !log.Contains("UnexpectedException"))
                throw new Exception("安全日志记录了敏感调用字符串。");
        }
        finally {LogManager.Configuration=previous;}
        Console.WriteLine("敏感导出通过：后台明文授权、临时目录拒绝、加密／恢复、恶意文本保持字符串、负金额保持数值及日志允许字段。");
        static void RejectPath(string path)
        {try {ExportDestinationPolicy.Validate(path,true,false);} catch(CrsException) {return;} throw new Exception("临时目录别名绕过明文策略。");}
    }
    private static async Task Reject(Task task)
    {try {await task;} catch(CrsException) {return;} throw new Exception("未授权明文未拒绝。");}
}
