using System.Security.Cryptography;
using System.Text.Json;

namespace CRS.Serialization;

/// <summary>传输有界JSON文档的连续块；哈希及总长绑定整个文档，不能将半份结果交给调用方。</summary>
public sealed record JsonDocumentBlock(int Offset,int TotalBytes,string Sha256,byte[] Content,int FormatVersion=1);

public static class JsonDocumentBlocks
{
    public const int BlockBytes=8192;
    public const int DefaultMaximumBytes=1024*1024;
    private static readonly JsonSerializerOptions Options=new()
        {MaxDepth=32,RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true};
    public static IReadOnlyList<JsonDocumentBlock> Encode<T>(T value,CancellationToken token,int maximumBytes=DefaultMaximumBytes) where T:notnull
    {
        ValidateLimit(maximumBytes);token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(value);
        using var buffer=new BoundedWriteStream(maximumBytes);
        JsonSerializer.Serialize(buffer,value,Options);var bytes=buffer.ToArray();
        var hash=Convert.ToHexString(SHA256.HashData(bytes));var result=new List<JsonDocumentBlock>();
        for(var offset=0;offset<bytes.Length;offset+=BlockBytes)
        {token.ThrowIfCancellationRequested();result.Add(new(offset,bytes.Length,hash,bytes.AsSpan(offset,Math.Min(BlockBytes,bytes.Length-offset)).ToArray()));}
        return result.AsReadOnly();
    }
    public static T Decode<T>(IReadOnlyList<JsonDocumentBlock> blocks,CancellationToken token,int maximumBytes=DefaultMaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(blocks);ValidateLimit(maximumBytes);token.ThrowIfCancellationRequested();
        if(blocks.Count==0||blocks.Count>(maximumBytes+BlockBytes-1)/BlockBytes)throw new InvalidDataException("研究文档块数量无效。");
        var first=blocks[0]??throw new InvalidDataException("研究文档首块缺失。");
        if(first.TotalBytes is <1||first.TotalBytes>maximumBytes||first.Sha256 is not {Length:64}||!first.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("研究文档身份或长度无效。");
        using var stream=new MemoryStream(first.TotalBytes);
        foreach(var block in blocks)
        {
            token.ThrowIfCancellationRequested();
            if(block is null||block.FormatVersion!=1||block.Offset!=stream.Length||block.TotalBytes!=first.TotalBytes
                ||!string.Equals(block.Sha256,first.Sha256,StringComparison.OrdinalIgnoreCase)||block.Content is not {Length:>0 and <=BlockBytes}
                ||stream.Length+block.Content.Length>first.TotalBytes)
                throw new InvalidDataException("研究文档块不连续、版本不符或混入其他文档。");
            stream.Write(block.Content);
        }
        var bytes=stream.ToArray();
        if(bytes.Length!=first.TotalBytes||!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),first.Sha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("研究文档不完整或内容哈希不符。");
        token.ThrowIfCancellationRequested();
        try{return JsonSerializer.Deserialize<T>(bytes,Options)??throw new InvalidDataException("研究文档为空。");}
        catch(JsonException error){throw new InvalidDataException("研究文档结构无效。",error);}
    }
    private static void ValidateLimit(int maximumBytes)
    {if(maximumBytes is <1 or >8*1024*1024)throw new ArgumentOutOfRangeException(nameof(maximumBytes));}
    private sealed class BoundedWriteStream(int limit):MemoryStream(Math.Min(limit,BlockBytes))
    {
        private void Require(int count)
        {if(Length+count>limit)throw new InvalidDataException("研究文档超过传输限额，请缩小日期范围。");}
        public override void Write(byte[] buffer,int offset,int count){Require(count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Require(buffer.Length);base.Write(buffer);}
        public override void WriteByte(byte value){Require(1);base.WriteByte(value);}
    }
}
