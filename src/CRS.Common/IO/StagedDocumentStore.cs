using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Common.Integrity;

namespace CRS.IO.Staging;

public sealed record StagedDocumentReference(Guid Id,string Sha256,int Length,string Purpose);
public sealed record DocumentUploadRequest(string Action,StagedDocumentReference Document,int Offset=0,byte[]? Content=null);
public sealed record StagedDocumentStatus(StagedDocumentReference Document,string State,int NextOffset,
    DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,string? Consumer=null);

/// <summary>普通文件暂存机制：固定块、有界总量、持久进度及不可变封存；不包含插件或金融规则。</summary>
public sealed class StagedDocumentStore(string root,TimeProvider? timeProvider=null)
{
    public const int ChunkBytes=16384;
    public const int MaximumBytes=8*1024*1024;
    private const int MaximumActive=16;
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private static readonly JsonSerializerOptions Json=new(){RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=12};
    private sealed record Manifest(int Version,string Owner,StagedDocumentStatus Status,string Hash);
    public IReadOnlyList<StagedDocumentStatus> List(string owner,CancellationToken token)
    {
        using var lease=Lock(token);return Manifests().Where(x=>x.Owner==owner).Select(x=>x.Status).OrderBy(x=>x.CreatedAt).ToArray();
    }
    public StagedDocumentStatus Status(Guid id,string owner,CancellationToken token)
    {using var lease=Lock(token);var value=Load(id);Own(value,owner);return value.Status;}
    public StagedDocumentStatus Apply(DocumentUploadRequest request,string owner,CancellationToken token)
    {
        Validate(request.Document);if(string.IsNullOrWhiteSpace(owner)||owner.Length>200)throw new InvalidDataException("暂存所有者无效。");
        using var lease=Lock(token);var id=request.Document.Id;Manifest value;
        if(request.Action=="Begin")
        {
            if(request.Offset!=0||request.Content is not null)throw new InvalidDataException("暂存登记参数无效。");
            if(File.Exists(Metadata(id)))value=Load(id);
            else
            {
                var entries=Manifests().ToArray();
                if(entries.Count(x=>x.Status.State is "Uploading" or "Ready")>=MaximumActive||ReservedBytes(entries)+request.Document.Length>(long)MaximumActive*MaximumBytes)throw new InvalidOperationException("待处理文件已达到暂存限额，请先处理或丢弃旧文件。");
                var now=clock.GetUtcNow();value=Create(owner,new(request.Document,"Uploading",0,now,now));
                Directory.CreateDirectory(DirectoryFor(id));Save(value);
            }
            Own(value,owner);Same(value.Status.Document,request.Document);return value.Status;
        }
        value=Load(id);Own(value,owner);Same(value.Status.Document,request.Document);
        if(request.Action=="Chunk")
        {
            if(value.Status.State!="Uploading"||request.Content is not {Length:>0 and <=ChunkBytes}||request.Offset<0||request.Offset%ChunkBytes!=0
                ||request.Offset>value.Status.NextOffset||request.Offset+request.Content.Length>request.Document.Length
                ||request.Content.Length!=Math.Min(ChunkBytes,request.Document.Length-request.Offset))throw new InvalidDataException("暂存块位置、长度或状态不符。");
            var target=Block(id,request.Offset);token.ThrowIfCancellationRequested();
            if(File.Exists(target))
            {RejectLinks(target);if(new FileInfo(target).Length!=request.Content.Length||!File.ReadAllBytes(target).AsSpan().SequenceEqual(request.Content))throw new InvalidDataException("已有暂存块内容不同，拒绝覆盖。");}
            else
            {
                if(request.Offset<value.Status.NextOffset)throw new InvalidDataException("已经确认的暂存块缺失。");
                var temporary=target+".new";if(File.Exists(temporary)){RejectLinks(temporary);File.Delete(temporary);}WriteNew(temporary,request.Content);File.Move(temporary,target);
            }
            if(request.Offset==value.Status.NextOffset){value=Create(owner,value.Status with{NextOffset=request.Offset+request.Content.Length,UpdatedAt=clock.GetUtcNow()});Save(value);}
            return value.Status;
        }
        if(request.Content is not null||request.Offset!=0)throw new InvalidDataException("暂存操作参数无效。");
        if(request.Action=="Seal")
        {
            if(value.Status.State is not ("Uploading" or "Ready")||value.Status.NextOffset!=request.Document.Length)throw new InvalidDataException("文件尚未完整提交。");
            _=ReadContent(value.Status.Document,token);
            if(value.Status.State!="Ready"){value=Create(owner,value.Status with{State="Ready",UpdatedAt=clock.GetUtcNow()});Save(value);}return value.Status;
        }
        if(request.Action=="Discard")
        {
            if(value.Status.State=="Consumed"){DeleteBlocks(id);return value.Status;}
            value=Create(owner,value.Status with{State="Discarded",UpdatedAt=clock.GetUtcNow()});Save(value);DeleteBlocks(id);return value.Status;
        }
        throw new InvalidDataException("未知暂存操作。");
    }
    public byte[] ReadReady(StagedDocumentReference reference,string expectedPurpose,CancellationToken token)
    {
        Validate(reference);if(reference.Purpose!=expectedPurpose)throw new InvalidDataException("暂存文件用途不匹配。");
        using var lease=Lock(token);var value=Load(reference.Id);Same(value.Status.Document,reference);
        if(value.Status.State!="Ready")throw new InvalidDataException("暂存文件未封存或已经使用。");return ReadContent(reference,token);
    }
    public void Consume(StagedDocumentReference reference,Guid command,CancellationToken token)
    {
        Validate(reference);if(command==Guid.Empty)throw new InvalidDataException("暂存消费命令缺失。");
        using var lease=Lock(token);var value=Load(reference.Id);Same(value.Status.Document,reference);
        if(value.Status.State=="Consumed"&&value.Status.Consumer==command.ToString("D")){DeleteBlocks(reference.Id);return;}
        if(value.Status.State!="Ready")throw new InvalidDataException("暂存文件已被其他操作使用。");
        value=Create(value.Owner,value.Status with{State="Consumed",Consumer=command.ToString("D"),UpdatedAt=clock.GetUtcNow()});Save(value);DeleteBlocks(reference.Id);
    }
    private byte[] ReadContent(StagedDocumentReference reference,CancellationToken token)
    {
        var result=new byte[reference.Length];
        for(var offset=0;offset<result.Length;offset+=ChunkBytes)
        {
            token.ThrowIfCancellationRequested();var path=Block(reference.Id,offset);RejectLinks(path);var expected=Math.Min(ChunkBytes,result.Length-offset);
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(input.Length!=expected)throw new InvalidDataException("暂存块长度损坏。");input.ReadExactly(result.AsSpan(offset,expected));
        }
        if(!ContentFingerprint.Matches(reference.Sha256,result))throw new InvalidDataException("暂存文件摘要不符。");return result;
    }
    private IDisposable Lock(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();RejectLinks(root);Directory.CreateDirectory(root);RejectLinks(root);
        // 跨宿主/执行进程互斥，失败明确返回Busy；文件句柄随进程退出自动释放。
        var lockFile=Path.Combine(root,"store.lock");RejectLinks(lockFile);
        try{return new FileStream(lockFile,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException error){throw new InvalidOperationException("暂存存储正在使用。",error);}
    }
    private IEnumerable<Manifest> Manifests()
    {
        var count=0;foreach(var directory in Directory.EnumerateDirectories(root))
        {
            if(++count>10000)throw new InvalidDataException("暂存记录数量超过有界读取范围。");
            if(!Guid.TryParseExact(Path.GetFileName(directory),"N",out var id))throw new InvalidDataException("暂存目录身份无效。");
            RejectLinks(directory);
            if(!File.Exists(Metadata(id)))
            {
                // 首次登记在原子替换前中断时尚未接受任何块；允许原ID重新登记，保留未确认的临时字节。
                if(Directory.EnumerateFiles(directory).Any(x=>Path.GetFileName(x)!="manifest.json.new"))throw new InvalidDataException("已有暂存块却缺少登记。");
                continue;
            }
            yield return Load(id);
        }
    }
    private Manifest Load(Guid id)
    {
        if(id==Guid.Empty)throw new InvalidDataException("暂存标识缺失。");var path=Metadata(id);RejectLinks(path);
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(input.Length is <1 or >4096)throw new InvalidDataException("暂存登记长度异常。");
        Manifest value;try{value=JsonSerializer.Deserialize<Manifest>(input,Json)??throw new InvalidDataException("暂存登记为空。");}catch(JsonException error){throw new InvalidDataException("暂存登记损坏。",error);}
        var status=value.Status;Validate(status.Document);
        if(value.Version!=1||status.Document.Id!=id||value.Hash!=Digest(value.Owner,status)||status.CreatedAt==default||status.UpdatedAt<status.CreatedAt
            ||status.NextOffset<0||status.NextOffset>status.Document.Length||status.NextOffset!=status.Document.Length&&status.NextOffset%ChunkBytes!=0
            ||status.State is not ("Uploading" or "Ready" or "Consumed" or "Discarded")||status.State is "Ready" or "Consumed"&&status.NextOffset!=status.Document.Length)
            throw new InvalidDataException("暂存登记身份、进度或状态不符。");
        if(status.State=="Consumed"&&!Guid.TryParseExact(status.Consumer,"D",out _)||status.State!="Consumed"&&status.Consumer is not null)throw new InvalidDataException("暂存消费身份不符。");return value;
    }
    private long ReservedBytes(IReadOnlyList<Manifest> entries)
    {
        long total=0;foreach(var entry in entries)
        {
            if(entry.Status.State is "Uploading" or "Ready"){total+=entry.Status.Document.Length;continue;}
            var files=0;foreach(var path in Directory.EnumerateFiles(DirectoryFor(entry.Status.Document.Id),"*.block"))
            {if(++files>MaximumBytes/ChunkBytes)throw new InvalidDataException("暂存块数量异常。");RejectLinks(path);total+=new FileInfo(path).Length;}
        }
        return total;
    }
    private void Save(Manifest value)
    {
        var path=Metadata(value.Status.Document.Id);RejectLinks(path);var temporary=path+".new";
        if(File.Exists(temporary)){RejectLinks(temporary);File.Delete(temporary);}
        WriteNew(temporary,JsonSerializer.SerializeToUtf8Bytes(value,Json));File.Move(temporary,path,true);
    }
    private static Manifest Create(string owner,StagedDocumentStatus status)=>new(1,owner,status,Digest(owner,status));
    private static string Digest(string owner,StagedDocumentStatus status)=>ContentFingerprint.Sha256(JsonSerializer.SerializeToUtf8Bytes(new{Version=1,Owner=owner,Status=status}));
    private static void Same(StagedDocumentReference expected,StagedDocumentReference actual){if(expected!=actual)throw new InvalidDataException("暂存标识已绑定其他文件。");}
    private static void Own(Manifest value,string owner){if(value.Owner!=owner)throw new UnauthorizedAccessException("暂存文件不属于当前提交者。");}
    private static void Validate(StagedDocumentReference reference)
    {
        if(reference is null||reference.Id==Guid.Empty||!ContentFingerprint.IsSha256Hex(reference.Sha256)||reference.Sha256!=reference.Sha256.ToUpperInvariant()||reference.Length is <1 or >MaximumBytes
            ||reference.Purpose is not {Length:>0 and <=80}||reference.Purpose.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '.' and not '-'))throw new InvalidDataException("暂存文件摘要、用途或长度无效。");
    }
    private string DirectoryFor(Guid id)=>Path.Combine(Path.GetFullPath(root),id.ToString("N"));
    private string Metadata(Guid id)=>Path.Combine(DirectoryFor(id),"manifest.json");
    private string Block(Guid id,int offset)=>Path.Combine(DirectoryFor(id),offset.ToString("D8",CultureInfo.InvariantCulture)+".block");
    private static void WriteNew(string path,byte[] bytes){RejectLinks(path);using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(bytes);output.Flush(true);}
    private void DeleteBlocks(Guid id)
    {
        var directory=DirectoryFor(id);RejectLinks(directory);
        foreach(var path in Directory.EnumerateFiles(directory,"*.block")){RejectLinks(path);File.Delete(path);}
    }
    private static void RejectLinks(string path)
    {
        _=CRS.IO.PhysicalPathGuard.RejectLinks(path);
    }
}
