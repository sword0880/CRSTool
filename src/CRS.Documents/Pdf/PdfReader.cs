using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace CRS.Documents.Pdf;

/// <summary>独立于业务的 PDF 字符坐标，使用 PDF 页面的左下角坐标系。</summary>
public sealed record PdfLetter(string Value, double X, double BaselineY, double Right);
/// <summary>独立于 PdfPig 对象生命周期的单词和边界信息。</summary>
public sealed record PdfWord(string Text, double Left, double Bottom, double Right, double Top);
/// <summary>供各业务解析器复用的页面文字与坐标，底层文档关闭后仍可使用。</summary>
public sealed record PdfPageContent(int Number, double Width, double Height,
    IReadOnlyList<PdfLetter> Letters, IReadOnlyList<PdfWord> Words);

/// <summary>公共 PdfPig 读取入口；统一文件限额、资源释放、取消和中文异常。</summary>
public static class PdfReader
{
    public const int MaxFileBytes = 25 * 1024 * 1024;
    public const int MaxPages = 200;
    private const int MaxLetters = 2_000_000;

    /// <summary>读取本地 PDF，并将其转换成不依赖 PdfPig 的页面事实。</summary>
    public static IReadOnlyList<PdfPageContent> Read(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new InvalidDataException("PDF 文件不存在。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("PDF 文件必须非空且不能超过 25 MB。");
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if (memory.Length + read > MaxFileBytes) throw new InvalidDataException("PDF 文件必须非空且不能超过 25 MB。");
            memory.Write(buffer, 0, read);
        }
        return Read(memory.ToArray(), cancellation);
    }

    /// <summary>读取内存 PDF，供下载、上传及其他券商适配器共用；不支持 OCR。</summary>
    public static IReadOnlyList<PdfPageContent> Read(byte[] content, CancellationToken cancellation = default)
        => ReadCore(content, null, cancellation);

    /// <summary>只解析指定物理页，页码保持原件位置；不放宽全文件及页数限额。</summary>
    public static IReadOnlyList<PdfPageContent> ReadSelected(byte[] content, IReadOnlyList<int> pages, CancellationToken cancellation = default)
    {
        if (pages.Count is < 1 or > 32 || pages.Any(x => x < 1) || pages.Distinct().Count() != pages.Count)
            throw new InvalidDataException("PDF 选择页必须为不重复的正整数，且不超过32页。");
        return ReadCore(content, pages.Order().ToArray(), cancellation);
    }
    private static IReadOnlyList<PdfPageContent> ReadCore(byte[] content, int[]? selected, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (content.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("PDF 内容必须非空且不能超过 25 MB。");
        try
        {
            using var document = PdfDocument.Open(content);
            if (document.NumberOfPages > MaxPages) throw new InvalidDataException("PDF 不能超过 200 页。");
            if (selected?.Any(x => x > document.NumberOfPages) == true) throw new InvalidDataException("PDF 选择页超出原件范围。");
            var result = new List<PdfPageContent>(); var total = 0;
            var numbers = selected ?? Enumerable.Range(1, document.NumberOfPages).ToArray();
            foreach (var number in numbers)
            {
                cancellation.ThrowIfCancellationRequested();
                var page = document.GetPage(number);
                total += page.Letters.Count;
                if (total > MaxLetters) throw new InvalidDataException("PDF 文字数量过多，请拆分文件。");
                var letters = page.Letters.Select(l => new PdfLetter(l.Value, l.StartBaseLine.X, l.StartBaseLine.Y, l.BoundingBox.Right)).ToArray();
                var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Select(w => new PdfWord(w.Text,
                    w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)).ToArray();
                result.Add(new(page.Number, page.Width, page.Height, letters, words));
            }
            return result;
        }
        catch (Exception error) when (error is not (InvalidDataException or OperationCanceledException))
        { throw new InvalidDataException("PDF 读取失败，请检查文件是否损坏、加密或使用了无法识别的格式。", error); }
    }
}
