using System.Text;
using CRS.Common.Integrity;
using CRS.IO.Staging;
using CRS.Serialization;

internal static class StagedDocumentChecks
{
    public static void Run(string root)
    {
        Directory.CreateDirectory(root);var bytes=Enumerable.Range(0,40001).Select(x=>(byte)(x%251)).ToArray();
        var reference=new StagedDocumentReference(Guid.NewGuid(),ContentFingerprint.Sha256(bytes),bytes.Length,"test-original-v1");var store=new StagedDocumentStore(Path.Combine(root,"main"));
        var first=store.Apply(new("Begin",reference),"alice",default);Check(first is{State:"Uploading",NextOffset:0},"登记只建立进度，不冒充完整原件");
        Reject<InvalidDataException>(()=>store.Apply(new("Seal",reference),"alice",default),"缺块不能封存");
        Reject<UnauthorizedAccessException>(()=>store.Status(reference.Id,"bob",default),"其他所有者不能读取进度");
        Reject<InvalidDataException>(()=>store.Apply(new("Begin",reference with{Length=1}),"alice",default),"同标识不能更换长度或摘要");
        var block=bytes[..StagedDocumentStore.ChunkBytes];store.Apply(new("Chunk",reference,0,block),"alice",default);
        var restart=new StagedDocumentStore(Path.Combine(root,"main"));Check(restart.Apply(new("Begin",reference),"alice",default).NextOffset==block.Length,"重启按持久进度续传");
        Check(restart.Apply(new("Chunk",reference,0,block),"alice",default).NextOffset==block.Length,"块响应丢失重放不重复追加");
        var changed=block.ToArray();changed[0]^=1;Reject<InvalidDataException>(()=>restart.Apply(new("Chunk",reference,0,changed),"alice",default),"已确认块不能换内容");
        Reject<InvalidDataException>(()=>restart.Apply(new("Chunk",reference,32768,bytes[32768..]),"alice",default),"跳块拒绝");
        Reject<InvalidDataException>(()=>restart.ReadReady(reference,"test-original-v1",default),"未封存文件不能交给后台解析");
        for(var offset=block.Length;offset<bytes.Length;offset+=StagedDocumentStore.ChunkBytes)restart.Apply(new("Chunk",reference,offset,bytes.AsSpan(offset,Math.Min(StagedDocumentStore.ChunkBytes,bytes.Length-offset)).ToArray()),"alice",default);
        Check(restart.Apply(new("Seal",reference),"alice",default).State=="Ready"&&restart.ReadReady(reference,"test-original-v1",default).AsSpan().SequenceEqual(bytes),"连续完整字节与整体摘要一致后才封存");
        Reject<InvalidDataException>(()=>restart.ReadReady(reference,"another-purpose",default),"原件用途不能互换");
        Reject<InvalidDataException>(()=>restart.Apply(new("Chunk",reference,0,block),"alice",default),"封存后不可写入");
        var consumer=Guid.NewGuid();restart.Consume(reference,consumer,default);restart.Consume(reference,consumer,default);
        Check(restart.Status(reference.Id,"alice",default) is{State:"Consumed",Consumer:not null}&&!Directory.EnumerateFiles(Path.Combine(root,"main",reference.Id.ToString("N")),"*.block").Any(),"消费与重放保留身份，删除暂存副本");
        Reject<InvalidDataException>(()=>restart.Consume(reference,Guid.NewGuid(),default),"其他命令不能冒充原消费");
        Reject<InvalidDataException>(()=>restart.ReadReady(reference,"test-original-v1",default),"已消费文件不再当作新输入");
        var corrupt=new StagedDocumentReference(Guid.NewGuid(),ContentFingerprint.Sha256(new byte[]{1}),1,"test-v1");restart.Apply(new("Begin",corrupt),"alice",default);restart.Apply(new("Chunk",corrupt,0,new byte[]{2}),"alice",default);
        Reject<InvalidDataException>(()=>restart.Apply(new("Seal",corrupt),"alice",default),"整体摘要错误不封存");restart.Apply(new("Discard",corrupt),"alice",default);
        Check(restart.Status(corrupt.Id,"alice",default).State=="Discarded","中断或无效暂存可明确丢弃，保留登记");
        Reject<InvalidDataException>(()=>restart.Apply(new("Begin",reference with{Id=Guid.Empty}),"alice",default),"空标识拒绝");
        Reject<InvalidDataException>(()=>restart.Apply(new("Begin",reference with{Length=StagedDocumentStore.MaximumBytes+1}),"alice",default),"单文件大小有界");
        var quota=new StagedDocumentStore(Path.Combine(root,"quota"));for(var i=0;i<16;i++)quota.Apply(new("Begin",reference with{Id=Guid.NewGuid(),Length=StagedDocumentStore.MaximumBytes}),"alice",default);
        Reject<InvalidOperationException>(()=>quota.Apply(new("Begin",reference with{Id=Guid.NewGuid()}),"alice",default),"活动文件数及预留空间有界");
        using(var held=new FileStream(Path.Combine(root,"main","store.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))Reject<InvalidOperationException>(()=>restart.List("alice",default),"跨实例占用返回Busy而非覆盖状态");
        var interrupted=reference with{Id=Guid.NewGuid()};var folder=Path.Combine(root,"main",interrupted.Id.ToString("N"));Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"manifest.json.new"),"partial");
        Check(restart.Apply(new("Begin",interrupted),"alice",default).NextOffset==0,"首次登记原子替换前中断可恢复");
        File.WriteAllBytes(Path.Combine(folder,"00000000.block"),block);Check(restart.Apply(new("Chunk",interrupted,0,block),"alice",default).NextOffset==block.Length,"块写入完成但进度未确认可重放恢复");
        var metadata=Path.Combine(folder,"manifest.json");var original=File.ReadAllText(metadata);File.WriteAllText(metadata,original.Replace("Uploading","Ready",StringComparison.Ordinal));
        Reject<InvalidDataException>(()=>restart.Status(interrupted.Id,"alice",default),"登记状态改写不能绕过摘要");File.WriteAllText(metadata,original);
        var cancelled=false;try{restart.Apply(new("Begin",reference with{Id=Guid.NewGuid()}),"alice",new(true));}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"取消不伪造成功登记");
        Reject<InvalidDataException>(()=>StrictJson.Read<Dictionary<string,int>>("{\"x\":1,\"x\":2}",100),"公共JSON入口拒绝重复字段");
        Console.WriteLine("PASS 文件暂存专项：合成文件、私有隔离目录，不读取正式资料。");
    }
    private static void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 文件暂存："+text);}
    private static void Reject<T>(Action action,string text) where T:Exception{try{action();}catch(T){Check(true,text);return;}throw new Exception("未拒绝："+text);}
}
