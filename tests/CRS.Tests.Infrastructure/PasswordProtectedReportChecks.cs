using System.Text;
using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;
using CRS.Documents;
using CRS.Documents.Excel;
using CRS.Documents.Pdf;
using NPOI.POIFS.Crypt;
using NPOI.POIFS.FileSystem;

/// <summary>用真实文件验证原生打开密码、独立 PDF 读取、金额语义及取消／锁定时保留原文件。</summary>
internal static class PasswordProtectedReportChecks
{
    public static async Task RunAsync(string root)
    {
        var directory=Path.Combine(root,"password-documents"); Directory.CreateDirectory(directory);
        const string password="合成密码-Export-2026",marker="PRIVATE-TAX-ACCOUNT";
        var result=new CalculationResult {Year=2025,SnapshotId=marker,Broker="IBKR",CoveredAccounts=[marker],
            Summary=new(123.45m,0,0,24.69m,0,0,24.69m),EstimatedTopUpCny=24.69m,UnknownGainCount=1};
        var reviews=new[]{new ReviewNote(marker,"合成复核",string.Concat(Enumerable.Repeat("中文长复核说明，用于验证自动换行和多页内容完整。",100)),"2026-11-05T00:00:00Z")};
        var exporter=new ExcelReportExporter();
        // 模拟生成器退化成明文：保存前的独立读取验证必须拒绝，而不能依赖按钮状态。
        using(var plainExcel=new MemoryStream())
        using(var writer=new ExcelWriter())
        {
            writer.AddSheet("合成",["字段"],[[marker]]);writer.Write(plainExcel);
            Reject<InvalidDataException>(()=>PasswordDocumentVerification.Excel(plainExcel,password));
        }
        using(var plainPdf=new MemoryStream())
        {
            PdfTextWriter.Write(plainPdf,[new PdfParagraph(marker)]);
            Reject<InvalidDataException>(()=>PasswordDocumentVerification.Pdf(plainPdf,password));
        }
        var excel=Path.Combine(directory,"tax.xlsx"); exporter.ExportPasswordProtected(excel,result,TaxDocumentFormat.Excel,password,reviews);
        using(var file=File.OpenRead(excel))
        {
            var filesystem=new POIFSFileSystem(file);
            var info=new EncryptionInfo(filesystem);
            if(info.Header.KeySize!=256) throw new Exception("Excel 未使用 AES-256。");
            var decryptor=Decryptor.GetInstance(info);
            if(decryptor.VerifyPassword("") || decryptor.VerifyPassword("wrong-password")) throw new Exception("Excel 空密码／错误密码仍可通过。");
            if(!decryptor.VerifyPassword(password)) throw new Exception("Excel 正确密码无法通过。");
            using var decrypted=decryptor.GetDataStream(filesystem);
            using var workbook=new NPOI.XSSF.UserModel.XSSFWorkbook(decrypted);
            var summary=workbook.GetSheet("税务汇总");
            if(summary.GetRow(1).GetCell(1).NumericCellValue!=123.45 || summary.GetRow(8).GetCell(1).StringCellValue!=""
                || !summary.GetRow(8).GetCell(2).StringCellValue.Contains("不可确定")
                || !workbook.GetSheet("人工复核").GetRow(1).GetCell(2).StringCellValue.Contains("中文长复核"))
                throw new Exception("加密 Excel 丢失金额、未知语义或人工复核。");
        }
        var pdf=Path.Combine(directory,"tax.pdf"); exporter.ExportPasswordProtected(pdf,result,TaxDocumentFormat.Pdf,password,reviews);
        RejectPdf(""); RejectPdf("wrong-password");
        using(var document=UglyToad.PdfPig.PdfDocument.Open(pdf,new UglyToad.PdfPig.ParsingOptions {Password=password}))
        {
            var text=string.Concat(document.GetPages().Select(p=>p.Text));
            if(document.GetPages().Any(p=>p.Letters.Any(l=>l.BoundingBox.Left<35 || l.BoundingBox.Right>560 || l.BoundingBox.Bottom<12 || l.BoundingBox.Top>800)))
                throw new Exception("PDF 文字越过页边距。");
            if(document.NumberOfPages<2 || !text.Contains(marker) || !text.Contains("123.45") || !text.Contains("不可确定")
                || !text.Contains("中文长复核") || !text.Contains("第 2 页")) throw new Exception("独立 PDF 读取丢失中文、金额、未知状态或分页。");
        }
        if(Encoding.UTF8.GetString(File.ReadAllBytes(pdf)).Contains(marker)) throw new Exception("加密 PDF 泄露账户文字。");
        foreach(var format in new[]{TaxDocumentFormat.Excel,TaxDocumentFormat.Pdf})
        {
            var target=format==TaxDocumentFormat.Excel?excel:pdf; var original=File.ReadAllBytes(target);
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            Reject<OperationCanceledException>(()=>exporter.ExportPasswordProtected(target,result,format,password,cancellation:cancelled.Token));
            Reject<CrsException>(()=>exporter.ExportPasswordProtected(target,result,format,"short"));
            // 在真正的最终提交守卫处锁定保险库，旧操作不得替换已有密文。
            using var vault=new VaultManager(); await vault.CreateAsync(Path.Combine(directory,"vault-"+format),"Synthetic-Vault-Password");
            await TestStores.Enroll(vault); var guard=vault.CaptureGuard(); var calls=0;
            var bound=new ExcelReportExporter(action=>{if(++calls==3) vault.Lock();guard(action);});
            Reject<CrsException>(()=>bound.ExportPasswordProtected(target,result,format,password));
            using var final=new CancellationTokenSource(); calls=0;
            var cancelBound=new ExcelReportExporter(action=>{if(++calls==3) final.Cancel();action();});
            Reject<OperationCanceledException>(()=>cancelBound.ExportPasswordProtected(target,result,format,password,cancellation:final.Token));
            if(!File.ReadAllBytes(target).SequenceEqual(original) || Directory.EnumerateFiles(directory,"*.tmp").Any())
                throw new Exception("取消／锁定覆盖原文件或留下暂存。");
        }
        Reject<CrsException>(()=>exporter.ExportPasswordProtected(pdf,result,TaxDocumentFormat.Pdf,new string('密',43)));
        Reject<CrsException>(()=>exporter.ExportPasswordProtected(excel,result,(TaxDocumentFormat)999,password));
        Reject<CrsException>(()=>exporter.ExportPasswordProtected(pdf,result,TaxDocumentFormat.Excel,password));
        // 独立验证 PDF 未将已确认零值和未知金额混为一谈。
        var zero=new CalculationResult {Year=2025,Summary=new(0,0,0,0,0,0,0),EstimatedTopUpCny=0,Reconciled=true,
            CalculationStatus=CalculationStatus.Completed,DataCompleteness=DataCompleteness.Confirmed,
            ReconciliationStatus=ReconciliationStatus.Matched,UsageLabel=UsageLabel.ReviewReady};
        var zeroPdf=Path.Combine(directory,"zero.pdf"); exporter.ExportPasswordProtected(zeroPdf,zero,TaxDocumentFormat.Pdf,password);
        using(var document=UglyToad.PdfPig.PdfDocument.Open(zeroPdf,new UglyToad.PdfPig.ParsingOptions {Password=password}))
            if(!string.Concat(document.GetPages().Select(p=>p.Text)).Contains("年度预计补税（状态允许时）：0.00")) throw new Exception("PDF 真实零值被当作未知。");
        Console.WriteLine("原生加密报税文件通过：Excel AES-256、空／错误密码拒绝、独立 PDF 解密与中文分页、未知／零值、短／超长密码及格式拒绝、真实锁定和最终取消保留旧文件。输出："+directory);
        void RejectPdf(string secret)
        {
            try {using var document=UglyToad.PdfPig.PdfDocument.Open(pdf,new UglyToad.PdfPig.ParsingOptions {Password=secret});}
            catch(UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException) {return;}
            throw new Exception("PDF 空／错误密码仍可读取。");
        }
    }
    private static void Reject<T>(Action action) where T:Exception
    {try {action();} catch(T) {return;} throw new Exception("预期拒绝："+typeof(T).Name);}
}
