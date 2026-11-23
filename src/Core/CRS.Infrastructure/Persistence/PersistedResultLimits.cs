using System.Text;
using System.Text.Json;

namespace CRS.Infrastructure;

/// <summary>持久化外层也有资源配额；先检查字节、深度和集合，再构造业务对象。</summary>
internal static class PersistedResultLimits
{
    internal const int MaxBytes = 128 * 1024 * 1024;
    private const int MaxRows = 200000;
    private sealed class Frame(bool array)
    {
        internal bool Array { get; } = array;
        internal int Count;
        internal HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    }
    internal static byte[] Encode(CalculationResult result)
    {
        using var buffer = new MemoryStream();
        using (var bounded = new LimitedWriteStream(buffer, MaxBytes)) JsonSerializer.Serialize(bounded, result);
        var bytes = buffer.ToArray(); Validate(bytes); return bytes;
    }
    internal static void Validate(ReadOnlySpan<byte> bytes, string label = "历史记录")
    {
        if (bytes.Length is <= 0 or > MaxBytes) throw new CrsException($"{label}为空或超过 128 MiB，未加载或保存。");
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
            var frames = new Stack<Frame>(); var total = 0; string property = "";
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            frames.Push(new(false));
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueSpan.Length > 768) throw new JsonException();
                    property = reader.GetString()!;
                    if (property.Length > 128 || !frames.Peek().Names.Add(property)) throw new JsonException();
                    continue;
                }
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) { frames.Pop(); continue; }
                if (frames.Count == 0) throw new JsonException();
                if (frames.Peek().Array && (++frames.Peek().Count > MaxRows || ++total > 1000000)) throw new JsonException();
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    frames.Push(new(reader.TokenType == JsonTokenType.StartArray));
                else if (reader.TokenType == JsonTokenType.String)
                {
                    // 快照正文有独立较大额度；普通名称和说明不允许借外层总限额无限增长。
                    var limit = reader.CurrentDepth == 1 && property == nameof(CalculationResult.CanonicalInputJson) ? 64 * 1024 * 1024
                        : reader.CurrentDepth == 1 && property == nameof(CalculationResult.SnapshotJson) ? 8 * 1024 * 1024 : 32768;
                    if (reader.ValueSpan.Length > (long)limit * 6) throw new JsonException();
                    var value = reader.GetString()!;
                    if (value.Length > limit || Encoding.UTF8.GetByteCount(value) > limit) throw new JsonException();
                }
            }
            if (frames.Count != 0) throw new JsonException();
        }
        catch (JsonException) { throw new CrsException($"{label}格式、嵌套深度、重复字段或集合／文本大小超限，未加载或保存。"); }
    }
}

/// <summary>序列化达到配额即停止，不能等整个大对象编码完成后再检查。</summary>
internal sealed class LimitedWriteStream(Stream inner, long limit) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > limit - inner.Length) throw new CrsException("序列化结果超出大小限制，未保存任何任务。");
        inner.Write(buffer);
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
