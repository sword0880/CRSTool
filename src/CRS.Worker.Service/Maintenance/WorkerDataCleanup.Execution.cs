using System.Text.Json;
using CRS.Maintenance;
namespace CRS.Worker.Service;

public sealed partial class WorkerDataCleanup
{
    private sealed record Retirement(int Format,Guid OperationId,string SnapshotId,string ArchiveHash,string ManifestHash);
    private void VerifySnapshot(Target target,bool allowMissing,CancellationToken token)
    {
        StorageSecurity.RejectLinks(target.FullPath);
        var expected=target.ExpectedHashes??throw new InvalidDataException("缺少备份文件证明。");var files=Files(target.FullPath,token);
        if(!allowMissing&&files.Count!=expected.Count)throw new InvalidDataException("备份文件集合已改变。");
        foreach(var file in files)
        {
            var relative=Path.GetRelativePath(target.FullPath,file.FullName).Replace('\\','/');
            if(!expected.TryGetValue(relative,out var hash)||!Hash(file.FullName,token).Equals(hash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("备份文件已改变或出现未登记文件。");
        }
    }
    private void AddRemainders(List<DataCleanupEntry> entries,List<Target> targets,WorkerUpgradeRecord? current,CancellationToken token)
    {
        var trash=Child("maintenance/cleanup-trash");if(!Directory.Exists(trash))return;
        foreach(var directory in new DirectoryInfo(trash).EnumerateDirectories())
        {
            token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(directory.FullName);
            var entry=new DataCleanupEntry(directory.Name,"CleanupRemainder","上次清理的残留副本",Path.GetRelativePath(root,directory.FullName),null,null,false,false,"无法核实弃用记录，保留。");
            try
            {
                var receipt=ReadRetirement(directory.FullName);
                if(current?.Stage is not ("Committed" or "RolledBack")||receipt.SnapshotId==current.Id)throw new InvalidDataException("当前维护状态不允许继续清理。");
                var archive=Child("maintenance/"+receipt.SnapshotId+".json");
                if(!Hash(archive,token).Equals(receipt.ArchiveHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("历史维护记录已改变。");
                var record=ReadRecord(archive);if(record.BackupManifestHash!=receipt.ManifestHash||record.Stage is not ("Committed" or "RolledBack"))throw new InvalidDataException("弃用副本身份异常。");
                var backup=Path.Combine(directory.FullName,"backup");Target target;
                if(File.Exists(Path.Combine(backup,"snapshot.json")))target=InspectSnapshot(entry,backup,record,receipt.ArchiveHash,token,true);
                else
                {if(Directory.Exists(backup)&&Files(backup,token).Count!=0)throw new InvalidDataException("残留副本缺少清单。");target=new(entry,backup,receipt.ArchiveHash,receipt.ManifestHash,new(StringComparer.OrdinalIgnoreCase));}
                VerifySnapshot(target,true,token);
                entry=target.Entry with{Eligible=true,Selected=true,Bytes=target.Entry.Bytes??0,Files=target.Entry.Files??0,Reason="上次已确认弃用且移出回退位置的副本；可继续清理剩余文件。"};
                entries.Add(entry);targets.Add(target with{Entry=entry});
            }
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException){entries.Add(entry);}
        }
    }
    private Retirement ReadRetirement(string directory)
    {
        var path=Path.Combine(directory,"retired.json");StorageSecurity.RejectLinks(path);
        if(new FileInfo(path).Length>4096)throw new InvalidDataException("弃用记录超限。");
        var receipt=JsonSerializer.Deserialize<Retirement>(File.ReadAllBytes(path),Json)??throw new InvalidDataException("弃用记录缺失。");
        if(receipt.Format!=1||!Guid.TryParseExact(receipt.SnapshotId,"N",out _)||directory!=Child("maintenance/cleanup-trash/"+receipt.OperationId.ToString("N")+"-"+receipt.SnapshotId))throw new InvalidDataException("弃用记录路径不匹配。");
        var auditPath=Child("maintenance/cleanup/"+receipt.OperationId.ToString("N")+".json");
        if(new FileInfo(auditPath).Length>256*1024)throw new InvalidDataException("清理授权记录超限。");
        var audit=JsonSerializer.Deserialize<CleanupAudit>(File.ReadAllBytes(auditPath),Json)??throw new InvalidDataException("缺少已确认的清理记录。");
        if(audit.Format!=1||audit.Id!=receipt.OperationId||!audit.Status.Entries.Any(x=>x.Id==receipt.SnapshotId&&x.Selected&&x.Eligible))throw new InvalidDataException("弃用副本未被原清理操作选中。");
        foreach(var file in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {StorageSecurity.RejectLinks(file.FullName);if(file.Name!="retired.json"&&!(file.Name=="backup"&&file is DirectoryInfo))throw new InvalidDataException("弃用容器出现未知文件。");}
        return receipt;
    }
    private void Execute(Operation operation,Guid previewId,CancellationToken token)
    {
        try
        {
            StorageSecurity.RejectLinks(root);
            using var maintenanceLock=new FileStream(Child("maintenance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var journal=Child("maintenance/upgrade.json");var currentHash=File.Exists(journal)?Hash(journal,token):null;
            if(currentHash!=operation.CurrentJournalHash)throw new InvalidDataException("扫描后维护状态发生变化，请重新扫描。");
            var current=new WorkerUpgradeJournal(root).Read();
            if(operation.Targets.Any(x=>x.Entry.Category!="Logs"))
            {
                if(current?.Stage is not ("Committed" or "RolledBack"))throw new InvalidDataException("当前升级尚未完成。");
                foreach(var retained in operation.Retained)
                {if(Hash(Child("maintenance/"+retained.Entry.Id+".json"),token)!=retained.ArchiveHash)throw new InvalidDataException("保留副本的维护记录已改变。");VerifySnapshot(retained,false,token);}
            }
            foreach(var target in operation.Targets)
            {
                token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(target.FullPath);
                if(target.Entry.Category=="Logs")
                {
                    // 仅删除预览中已关闭、长度和更新时间均未变化的日志；不触及当前日志。
                    using(var input=new FileStream(target.FullPath,FileMode.Open,FileAccess.Read,FileShare.Delete))
                    {if(input.Length!=target.Length||File.GetLastWriteTimeUtc(target.FullPath).Ticks!=target.ModifiedTicks)throw new InvalidDataException("日志在扫描后被更新。");File.Delete(target.FullPath);}
                    Deleted(operation,target.Length);WriteAudit(operation,previewId);continue;
                }
                Target retired=target;string container;
                if(target.Entry.Category=="UpgradeBackup")
                {
                    if(target.Entry.Id==current!.Id)throw new InvalidDataException("禁止清理当前快照。");
                    var archive=Child("maintenance/"+target.Entry.Id+".json");
                    if(Hash(archive,token)!=target.ArchiveHash)throw new InvalidDataException("历史维护记录已改变。");
                    VerifySnapshot(target,false,token);
                    container=Child("maintenance/cleanup-trash/"+operation.Id.ToString("N")+"-"+target.Entry.Id);
                    Directory.CreateDirectory(container);StorageSecurity.RejectLinks(container);
                    AtomicJson(Path.Combine(container,"retired.json"),new Retirement(1,operation.Id,target.Entry.Id,target.ArchiveHash!,target.ManifestHash!));
                    var destination=Child(Path.GetRelativePath(root,Path.Combine(container,"backup")));
                    Directory.Move(target.FullPath,destination);retired=target with{FullPath=destination};
                }
                else{container=Directory.GetParent(target.FullPath)!.FullName;var receipt=ReadRetirement(container);if(receipt.SnapshotId==current!.Id)throw new InvalidDataException("禁止清理当前快照。");}
                VerifySnapshot(retired,true,token);
                foreach(var file in Files(retired.FullPath,token).OrderBy(x=>x.Name=="snapshot.json"?1:0))
                {
                    token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(file.FullName);
                    var relative=Path.GetRelativePath(retired.FullPath,file.FullName).Replace('\\','/');
                    if(!retired.ExpectedHashes!.TryGetValue(relative,out var expected)||Hash(file.FullName,token)!=expected.ToUpperInvariant())throw new InvalidDataException("弃用副本文件发生变化。");
                    var bytes=file.Length;File.Delete(file.FullName);Deleted(operation,bytes);
                }
                RemoveEmptyDirectories(retired.FullPath,token);StorageSecurity.RejectLinks(container);
                File.Delete(Path.Combine(container,"retired.json"));Directory.Delete(container,false);WriteAudit(operation,previewId);
            }
            Update(operation,"Completed","清理完成。已保留使用中的资料、维护记录和指定数量的历史回退副本。");
        }
        catch(OperationCanceledException){Update(operation,"Interrupted","清理中断；已清理的字节无法恢复，残留副本将在下次扫描中显示。");}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or OverflowException)
        {Update(operation,"Interrupted","清理未全部完成；部分文件可能已清理。请重新扫描核对剩余项。类别："+error.GetType().Name);}
        finally
        {try{WriteAudit(operation,previewId);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){Update(operation,"Interrupted","清理记录未能保存；请重新扫描核实，不能视为全部成功。");}finally{lock(stateGate){operation.Targets.Clear();operation.Retained.Clear();}}}
    }
    private void Deleted(Operation operation,long bytes)
    {lock(stateGate)operation.Status=operation.Status with{DeletedBytes=checked(operation.Status.DeletedBytes+bytes),DeletedFiles=operation.Status.DeletedFiles+1,UpdatedAt=Now};}
    private void RemoveEmptyDirectories(string directory,CancellationToken token)
    {
        if(!Directory.Exists(directory))return;
        var pending=new Stack<string>();var visited=new List<string>();pending.Push(directory);
        while(pending.TryPop(out var parent))
        {token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(parent);visited.Add(parent);foreach(var child in Directory.EnumerateDirectories(parent))pending.Push(child);if(visited.Count>MaximumFiles)throw new InvalidDataException("目录数量超限。");}
        foreach(var parent in visited.AsEnumerable().Reverse()){token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(parent);Directory.Delete(parent,false);}
    }
}
