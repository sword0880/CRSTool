using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Security.Cryptography;

namespace CRS.Infrastructure;

/// <summary>版本化加密底稿容器，正文仅在内存中组装；恢复只提交全新的目录。</summary>
public static class EncryptedReportFiles
{
    private const int MaxPlain=64*1024*1024,MaxContainer=96*1024*1024;
    private sealed record Container(string Format,PasswordPayload Payload);
    private sealed record Entry(string Name,byte[] Content);
    private sealed record Package(int Version,Entry[] Files);
    private static readonly JsonSerializerOptions Options=new() {UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters=true,MaxDepth=16};

    public static void Export(string path,CalculationResult result,string password,IEnumerable<ReviewNote>? reviews=null,
        CancellationToken cancellation=default,Action<Action>? guard=null)
    {
        cancellation.ThrowIfCancellationRequested(); guard?.Invoke(()=>cancellation.ThrowIfCancellationRequested());
        RunEvidenceSeal.Verify(result); result.Freeze();
        var entries=new List<Entry>(); byte[]? plain=null;
        try
        {
            PasswordEncryption.ValidatePassword(password);
            using(var workbook=new BoundedMemory(32*1024*1024,cancellation))
            {ExcelReports.Write(workbook,result,reviews,cancellation); entries.Add(new("report.xlsx",workbook.ToArray()));}
            if(result.CarryEligible)
            {
                entries.Add(new("carry.json",CarryFiles.Encode(CarryFiles.Create(result))));
                if(entries[^1].Content.Length>2*1024*1024) throw new CrsException("结转内容超过 2 MiB，不能生成可恢复的加密底稿。");
            }
            // 正文文件名和执行证据均处于密文内；外层不写年度、账户或业务摘要。
            entries.Add(new("evidence.json",JsonSerializer.SerializeToUtf8Bytes(new {result.SnapshotId,result.EvidenceSeal})));
            if(entries[^1].Content.Length>2*1024*1024) throw new CrsException("运行证据超过 2 MiB，不能生成可恢复的加密底稿。");
            using(var payload=new BoundedMemory(MaxPlain,cancellation))
            {JsonSerializer.Serialize(payload,new Package(1,entries.ToArray()),Options); plain=payload.ToArray();}
            cancellation.ThrowIfCancellationRequested();
            var sealedBody=PasswordEncryption.Seal(plain,password);
            cancellation.ThrowIfCancellationRequested();
            AtomicExport.Write(path,output=>JsonSerializer.Serialize(output,new Container("CRS.EncryptedExport.v1",sealedBody),Options),cancellation,guard);
        }
        catch(CryptographicException) {throw new CrsException("导出密码需为 12—1024 个字符，或加密操作失败。");}
        finally {if(plain is not null) CryptographicOperations.ZeroMemory(plain); foreach(var entry in entries) CryptographicOperations.ZeroMemory(entry.Content);}
    }

    /// <summary>用户选择父目录，认证和格式校验通过后才写明文；已有文件不被覆盖。</summary>
    public static string Restore(string path,string parentDirectory,string password,CancellationToken cancellation=default,Action<Action>? guard=null)
    {
        cancellation.ThrowIfCancellationRequested(); guard?.Invoke(()=>cancellation.ThrowIfCancellationRequested());
        // 尾部目录分隔符需统一，保证取消时的暂存目录归属比较一致。
        var parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory));
        if(!Directory.Exists(parent)) throw new CrsException("请选择已经存在的恢复父目录。");
        byte[]? plain=null; Package? package=null; string? stage=null;
        try
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(file.Length is <=0 or >MaxContainer) throw new CrsException("加密导出文件不能超过 96 MiB。");
                var bytes=new byte[(int)file.Length]; file.ReadExactly(bytes); cancellation.ThrowIfCancellationRequested();
                var container=JsonSerializer.Deserialize<Container>(bytes,Options);
                if(container is null || container.Format!="CRS.EncryptedExport.v1" || container.Payload is null
                    || container.Payload.Body?.Ciphertext is not {Length: >0 and <=MaxPlain}) throw new CrsException("加密导出格式无效。");
                plain=PasswordEncryption.Open(container.Payload,password);
            }
            cancellation.ThrowIfCancellationRequested(); package=JsonSerializer.Deserialize<Package>(plain,Options);
            if(package is null || package.Version!=1 || package.Files is not {Length: >=2 and <=3}
                || package.Files.Any(f=>f is null || f.Name is not ("report.xlsx" or "carry.json" or "evidence.json") || f.Content is not {Length: >0})
                || package.Files.Select(f=>f.Name).Distinct(StringComparer.Ordinal).Count()!=package.Files.Length
                || package.Files.Any(f=>f.Content.Length>(f.Name=="report.xlsx"?32*1024*1024:2*1024*1024))
                || !package.Files.Any(f=>f.Name=="report.xlsx") || !package.Files.Any(f=>f.Name=="evidence.json"))
                throw new CrsException("加密导出正文格式无效。");
            var token=Guid.NewGuid().ToString("N"); var target=Path.Combine(parent,"CRS-Restored-"+token);
            var pending=Path.Combine(parent,".crs-restore-"+token);
            if(Directory.Exists(pending) || File.Exists(pending)) throw new CrsException("恢复暂存位置已存在，请重新尝试。");
            Directory.CreateDirectory(pending); stage=pending;
            foreach(var entry in package.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                using var file=new FileStream(Path.Combine(stage,entry.Name),FileMode.CreateNew,FileAccess.Write,FileShare.None);
                for(var offset=0;offset<entry.Content.Length;offset+=65536)
                {cancellation.ThrowIfCancellationRequested(); file.Write(entry.Content,offset,Math.Min(65536,entry.Content.Length-offset));}
                file.Flush(true);
            }
            void Commit() {cancellation.ThrowIfCancellationRequested(); Directory.Move(stage,target);}
            if(guard is null) Commit(); else guard(Commit);
            stage=null; return target;
        }
        catch(CryptographicException) {throw new CrsException("密码错误或加密文件已损坏，未恢复任何资料。");}
        catch(JsonException) {throw new CrsException("加密导出格式无效，未完成恢复。");}
        finally
        {
            if(plain is not null) CryptographicOperations.ZeroMemory(plain);
            if(package?.Files is { } files) foreach(var entry in files) if(entry?.Content is { } content) CryptographicOperations.ZeroMemory(content);
            // 只删除本次暂存中允许的文件；不递归删除父目录或其他人新加的文件。
            if(stage is not null && Directory.Exists(stage) && Path.GetDirectoryName(stage)==parent)
            {
                foreach(var name in new[]{"report.xlsx","carry.json","evidence.json"})
                {var file=Path.Combine(stage,name); if(File.Exists(file)) File.Delete(file);}
                if(!Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage);
            }
        }
    }

    /// <summary>导出限量并响应取消；释放时尽力清零自己的内存缓冲。</summary>
    private sealed class BoundedMemory(int limit,CancellationToken cancellation):MemoryStream
    {
        private void Check(long length) {cancellation.ThrowIfCancellationRequested(); if(length>limit) throw new CrsException("加密导出内容超过允许大小，请减少明细。");}
        public override void Write(byte[] buffer,int offset,int count) {Check(Position+count); base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer) {Check(Position+buffer.Length); base.Write(buffer);}
        public override void WriteByte(byte value) {Check(Position+1); base.WriteByte(value);}
        public override void SetLength(long value) {Check(value); base.SetLength(value);}
        protected override void Dispose(bool disposing) {if(disposing && TryGetBuffer(out var buffer) && buffer.Array is { } bytes) CryptographicOperations.ZeroMemory(bytes); base.Dispose(disposing);}
    }
}
