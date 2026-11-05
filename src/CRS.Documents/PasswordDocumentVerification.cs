using NPOI.POIFS.Crypt;
using NPOI.POIFS.FileSystem;
using NPOI.XSSF.UserModel;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace CRS.Documents;

/// <summary>写出前使用读取路径复核密文；拒绝明文、空密码可读或不能用指定密码还原的输出。</summary>
public static class PasswordDocumentVerification
{
    public static void Excel(MemoryStream encrypted,string password,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();CheckSize(encrypted);
        if(encrypted.Length<8 || !encrypted.GetBuffer().AsSpan(0,8).SequenceEqual(new byte[]{0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1}))
            throw new InvalidDataException("生成的 Excel 不是加密工作簿，已停止保存。");
        using var input=new MemoryStream(encrypted.GetBuffer(),0,checked((int)encrypted.Length),false);
        var filesystem=new POIFSFileSystem(input);var info=new EncryptionInfo(filesystem);
        var decryptor=Decryptor.GetInstance(info);
        if(info.Header.KeySize!=256 || info.Header.CipherAlgorithm!=CipherAlgorithm.aes256
            || decryptor.VerifyPassword("") || !decryptor.VerifyPassword(password))
            throw new InvalidDataException("生成的 Excel 未通过文件打开密码验证，已停止保存。");
        using var content=decryptor.GetDataStream(filesystem);
        using var workbook=new XSSFWorkbook(content);
        cancellation.ThrowIfCancellationRequested();
    }
    public static void Pdf(MemoryStream encrypted,string password,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();CheckSize(encrypted);
        using var input=new MemoryStream(encrypted.GetBuffer(),0,checked((int)encrypted.Length),false);
        var rejectsEmpty=false;
        try {using var unprotected=PdfDocument.Open(input,new ParsingOptions {Password=""});}
        catch(PdfDocumentEncryptedException) {rejectsEmpty=true;}
        if(!rejectsEmpty) throw new InvalidDataException("生成的 PDF 无需密码即可打开，已停止保存。");
        using var correct=new MemoryStream(encrypted.GetBuffer(),0,checked((int)encrypted.Length),false);
        using var document=PdfDocument.Open(correct,new ParsingOptions {Password=password});
        if(document.NumberOfPages is <1 or >200) throw new InvalidDataException("生成的 PDF 页数无效，已停止保存。");
        foreach(var page in document.GetPages()) {cancellation.ThrowIfCancellationRequested();_ = page.Letters.Count;}
    }
    private static void CheckSize(MemoryStream content)
    {if(content.Length is <=0 or >64*1024*1024)throw new InvalidDataException("加密文档为空或超过 64 MiB，已停止保存。");}
}
