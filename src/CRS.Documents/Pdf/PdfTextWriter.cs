using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace CRS.Documents.Pdf;

/// <summary>调用方提供段落及标题级别，公共层只负责中文换行、分页和流输出。</summary>
public sealed record PdfParagraph(string Text, bool Heading = false);

/// <summary>生成嵌入字体的 A4 文字报告，不处理密码、文件路径或业务金额。</summary>
public static class PdfTextWriter
{
    /// <summary>生成带文件打开密码的 PDF，使用 AES-256；明文仅在内存生成，保持目标流打开。</summary>
    public static void WritePasswordProtected(Stream destination,IEnumerable<PdfParagraph> paragraphs,string password,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();ValidatePassword(password);
        using var plain=new PrivateBuffer();Write(plain,paragraphs,cancellation);plain.Position=0;
        using var document=PdfSharp.Pdf.IO.PdfReader.Open(plain,PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        document.SecuritySettings.UserPassword=password;document.SecuritySettings.OwnerPassword=password;
        document.SecurityHandler.SetEncryptionToV5();cancellation.ThrowIfCancellationRequested();
        // 调用方负责取消敏感文件的原子提交；此流包装同时中断 PDF 库的输出写入。
        using var ciphertext=new MemoryStream();
        using(var output=new CancellationOutput(ciphertext,cancellation))document.Save(output,false);
        PasswordDocumentVerification.Pdf(ciphertext,password,cancellation);ciphertext.Position=0;
        using var checkedOutput=new CancellationOutput(destination,cancellation);ciphertext.CopyTo(checkedOutput);
        cancellation.ThrowIfCancellationRequested();
    }
    /// <summary>PDF 2.0 密码最多取 127 个 UTF-8 字节，明确拒绝超长输入以防静默截断。</summary>
    public static void ValidatePassword(string password)
    {if(string.IsNullOrEmpty(password) || Encoding.UTF8.GetByteCount(password)>127) throw new ArgumentException("PDF 打开密码最多支持 127 个 UTF-8 字节（中文通常每字 3 字节），请使用较短密码。",nameof(password));}
    public static void Write(Stream destination, IEnumerable<PdfParagraph> paragraphs, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var typeface = SKFontManager.Default.MatchFamily("Microsoft YaHei")
            ?? throw new InvalidDataException("缺少 Microsoft YaHei 中文字体，无法生成 PDF。");
        using var normal = new SKFont(typeface, 11);
        using var heading = new SKFont(typeface, 15);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var document = SKDocument.CreatePdf(destination)
            ?? throw new InvalidDataException("无法创建 PDF 文档。");
        SKCanvas? canvas = null; float y = 0; var pages = 0; var characters = 0;
        void EndPage()
        {
            if (canvas is null) return;
            canvas.DrawText($"第 {pages} 页", 40, 815, normal, paint);
            document.EndPage(); canvas = null;
        }
        void BeginPage()
        {
            if (++pages > 200) throw new InvalidDataException("PDF 超过 200 页，请减少复核文字。");
            canvas = document.BeginPage(595, 842); y = 52;
        }
        void Line(string text, SKFont font)
        {
            cancellation.ThrowIfCancellationRequested();
            if (canvas is null) BeginPage();
            if (y + font.Size * 1.6f > 785) { EndPage(); BeginPage(); }
            y += font.Size * 1.6f;
            canvas!.DrawText(text, 40, y, font, paint);
        }
        foreach (var paragraph in paragraphs)
        {
            cancellation.ThrowIfCancellationRequested();
            characters += paragraph.Text.Length;
            if (characters > 2_000_000) throw new InvalidDataException("PDF 文字超过允许大小。");
            var font = paragraph.Heading ? heading : normal;
            if (canvas is not null) y += paragraph.Heading ? 12 : 5;
            foreach (var rawLine in paragraph.Text.Replace("\r", "").Split('\n'))
            {
                var line = ""; var elements = StringInfo.GetTextElementEnumerator(rawLine);
                while (elements.MoveNext())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var next = elements.GetTextElement();
                    if (line.Length > 0 && font.MeasureText(line + next) > 515) { Line(line, font); line = ""; }
                    line += next;
                }
                Line(line, font);
            }
        }
        EndPage(); document.Close(); cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>限制明文缓冲大小，释放时尽力清零；第三方内部副本由其自身管理。</summary>
    private sealed class PrivateBuffer:MemoryStream
    {
        public override void Write(byte[] buffer,int offset,int count)
        {
            if(Position+count>64*1024*1024)throw new InvalidDataException("PDF 内容超过 64 MiB，请减少文字。");
            base.Write(buffer,offset,count);
        }
        protected override void Dispose(bool disposing)
        {if(disposing&&TryGetBuffer(out var segment))CryptographicOperations.ZeroMemory(segment.AsSpan());base.Dispose(disposing);}
    }
    private sealed class CancellationOutput(Stream inner,CancellationToken cancellation):Stream
    {
        public override bool CanRead=>false;public override bool CanWrite=>inner.CanWrite;public override bool CanSeek=>inner.CanSeek;
        public override long Length=>inner.Length;public override long Position {get=>inner.Position;set=>inner.Position=value;}
        public override void Write(byte[] buffer,int offset,int count){cancellation.ThrowIfCancellationRequested();inner.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){cancellation.ThrowIfCancellationRequested();inner.Write(buffer);}
        public override void Flush(){cancellation.ThrowIfCancellationRequested();inner.Flush();}
        public override long Seek(long offset,SeekOrigin origin){cancellation.ThrowIfCancellationRequested();return inner.Seek(offset,origin);}
        public override void SetLength(long value){cancellation.ThrowIfCancellationRequested();inner.SetLength(value);}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
