namespace CRS.AI.Models.Attachments;

/// <summary>仅在当前进程内保存的附件解析结果；原始文件字节不会进入此对象。</summary>
public sealed class AiAttachmentDocument : IDisposable
{
    private readonly object gate = new();
    private char[] content;

    internal AiAttachmentDocument(Guid id, string fileName, string mediaType, int length, string sha256, string extractedText)
    {
        Id = id;
        FileName = fileName;
        MediaType = mediaType;
        Length = length;
        Sha256 = sha256;
        content = extractedText.ToCharArray();
    }

    public Guid Id { get; }
    public string FileName { get; }
    public string MediaType { get; }
    public int Length { get; }
    public string Sha256 { get; }
    public int CharacterCount { get { lock (gate) return content.Length; } }

    /// <summary>只向界面提供短预览，不暴露原始文件或完整解析文本。</summary>
    public string PreviewText
    {
        get
        {
            lock (gate) return new string(content.AsSpan(0, Math.Min(content.Length, AiAttachmentParser.PreviewCharacters)));
        }
    }

    /// <summary>在提交给模型或 MCP 会话上下文时创建短期字符串副本。</summary>
    public string ReadText()
    {
        lock (gate) return new string(content);
    }

    /// <summary>尽力清零可变字符缓冲并释放引用；CLR/模型序列化产生的副本不保证可擦除。</summary>
    public void Dispose()
    {
        lock (gate)
        {
            Array.Clear(content, 0, content.Length);
            content = Array.Empty<char>();
        }
    }
}
