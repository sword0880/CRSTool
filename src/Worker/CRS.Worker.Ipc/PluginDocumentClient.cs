using System.Text.Json;
using CRS.Common.Integrity;
using CRS.IO.Staging;
namespace CRS.Worker.Ipc;

/// <summary>受控本地文件读取与有界暂存提交；按完整摘要查找同所有者进度，重连及重启后可续传。</summary>
public sealed class PluginDocumentClient(BackgroundPipeClient connection)
{
    public async Task<StagedDocumentReference> UploadAsync(string plugin,string purpose,string path,CancellationToken token,IProgress<(int Received,int Total)>? progress=null)
    {
        byte[] bytes;await using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,8192,true))
        {
            if(file.Length is <1 or >StagedDocumentStore.MaximumBytes)throw new InvalidDataException("文件须为1字节至8MiB。");bytes=new byte[checked((int)file.Length)];await file.ReadExactlyAsync(bytes,token);
            if(file.Position!=file.Length)throw new InvalidDataException("文件读取期间发生变化。");
        }
        var hash=ContentFingerprint.Sha256(bytes);var pending=JsonSerializer.Deserialize<List<StagedDocumentStatus>>((await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"ListPluginDocuments",plugin),token)).Payload??"[]")??throw new InvalidDataException("暂存列表缺失。");
        var previous=pending.LastOrDefault(x=>x.Document.Sha256==hash&&x.Document.Length==bytes.Length&&x.Document.Purpose==purpose&&x.State is "Uploading" or "Ready");
        var reference=previous?.Document??new(Guid.NewGuid(),hash,bytes.Length,purpose);
        async Task<StagedDocumentStatus> Put(string action,int offset=0,byte[]? content=null)
        {
            var reply=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"PutPluginDocument",plugin,Payload:JsonSerializer.Serialize(new DocumentUploadRequest(action,reference,offset,content))),token);
            var status=JsonSerializer.Deserialize<StagedDocumentStatus>(reply.Payload??"")??throw new InvalidDataException("暂存回执缺失。");if(status.Document!=reference||status.NextOffset<0||status.NextOffset>bytes.Length)throw new InvalidDataException("暂存回执身份或长度不符。");return status;
        }
        var current=await Put("Begin");progress?.Report((current.NextOffset,bytes.Length));
        for(var offset=current.NextOffset;offset<bytes.Length;)
        {
            token.ThrowIfCancellationRequested();var count=Math.Min(StagedDocumentStore.ChunkBytes,bytes.Length-offset);var reply=await Put("Chunk",offset,bytes.AsSpan(offset,count).ToArray());
            if(reply.NextOffset!=offset+count)throw new InvalidDataException("暂存进度未连续前进。");offset=reply.NextOffset;progress?.Report((offset,bytes.Length));
        }
        var sealedFile=await Put("Seal");if(sealedFile.State!="Ready"||sealedFile.NextOffset!=bytes.Length)throw new InvalidDataException("文件尚未完整封存。");return reference;
    }
    private async Task<BackgroundResponse> Send(BackgroundRequest request,CancellationToken token)
    {
        while(true)
        {
            var reply=await connection.SendAsync(request,token);if(reply.Code is "RateLimited" or "Busy"){await Task.Delay(250,token);continue;}
            if(reply.Code!="Ok")throw new InvalidDataException("文件暂存不可用："+reply.Code);return reply;
        }
    }
}
