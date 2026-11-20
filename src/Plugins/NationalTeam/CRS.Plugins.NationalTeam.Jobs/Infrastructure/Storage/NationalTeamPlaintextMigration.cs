using CRS.IO;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed record NationalTeamMigrationFile(string Relative,bool Present,bool Converted,string? PreparedRelative,string? PlainSha256,
    IReadOnlyDictionary<string,string> Originals,NationalTeamPlaintextExport? Logical);
public sealed record NationalTeamMigrationRecord(int Version,Guid OperationId,string PluginRoot,string SnapshotRoot,string State,int Attempts,
    IReadOnlyList<NationalTeamMigrationFile> Files,DateTimeOffset RecordedAt);
public sealed record NationalTeamMigrationResult(Guid OperationId,string State,int ConvertedDatabases,IReadOnlyList<NationalTeamMigrationFile> Files);

/// <summary>国家队两库的离线保全切换；正式适配器仍须先停机并验证全量快照，不由普通启动自动调用。</summary>
public sealed partial class NationalTeamPlaintextMigration(Action<string>? checkpoint=null,TimeProvider? timeProvider=null)
{
    private static readonly string[] Databases=["data/market.db","data/source-policy.db"];
    private static readonly string[] Sidecars=["-wal","-shm",""];
    private DateTimeOffset Now=>CRS.Timing.BeijingClock.Normalize((timeProvider??TimeProvider.System).GetUtcNow());
    public NationalTeamMigrationResult Run(string pluginRoot,string snapshotRoot,Guid operationId,byte[] pluginKey,CancellationToken token=default)
    {
        pluginRoot=PhysicalPathGuard.RejectLinks(pluginRoot).TrimEnd(Path.DirectorySeparatorChar);snapshotRoot=PhysicalPathGuard.RejectLinks(snapshotRoot).TrimEnd(Path.DirectorySeparatorChar);
        if(operationId==Guid.Empty||pluginKey is not {Length:32}||!Directory.Exists(pluginRoot)||!Directory.Exists(snapshotRoot)
            ||Path.GetFileName(pluginRoot)!="crs.national-team"||Path.GetFileName(snapshotRoot)!="crs.national-team"||pluginRoot.Equals(snapshotRoot,StringComparison.OrdinalIgnoreCase)
            ||snapshotRoot.StartsWith(pluginRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||pluginRoot.StartsWith(snapshotRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("迁移主体、快照或操作身份无效。");
        token.ThrowIfCancellationRequested();
        using var ownership=new FileStream(PhysicalPathGuard.Child(pluginRoot,"runner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var operation=PhysicalPathGuard.Child(pluginRoot,"storage-migrations/"+operationId.ToString("N"));Directory.CreateDirectory(operation);
        var journal=PhysicalPathGuard.Child(operation,"migration.json");var purpose="NationalTeam.PlaintextMigration/v1/"+operationId.ToString("N");
        NationalTeamMigrationRecord record;
        if(File.Exists(journal))
        {
            record=AuthenticatedJsonFile.Read<NationalTeamMigrationRecord>(journal,pluginKey,purpose);Validate(record,pluginRoot,snapshotRoot,operationId);
        }
        else
        {
            if(Directory.EnumerateFileSystemEntries(operation).Any())throw new InvalidDataException("迁移目录存在未归属文件，保留现场。");
            record=new(1,operationId,pluginRoot,snapshotRoot,"Preparing",0,Databases.Select(r=>Capture(pluginRoot,snapshotRoot,r)).ToArray(),Now);
            AuthenticatedJsonFile.Write(journal,pluginKey,purpose,record);checkpoint?.Invoke("JournalCreated");
        }
        VerifySnapshot(record);
        if(record.State=="Completed")
        {
            // 完成后可能已有新事实；检查格式/完整性，不以旧摘要覆盖或否决合法新记录。
            foreach(var file in record.Files)
            {
                if(file.Converted)foreach(var pair in file.Originals)RequireHash(PhysicalPathGuard.Child(operation,"original/"+pair.Key),pair.Value);
                var live=PhysicalPathGuard.Child(pluginRoot,file.Relative);if(File.Exists(live))_=LegacyNationalTeamExport.InspectPlain(live);else if(file.Present)throw new InvalidDataException("已完成迁移的资料库缺失。");
            }
            return new(operationId,"AlreadyCompleted",record.Files.Count(x=>x.Converted),record.Files);
        }
        if(record.State=="Preparing")
        {
            if(record.Attempts>=8)throw new InvalidOperationException("准备重试次数已到上限，请核对保留现场。");
            foreach(var file in record.Files)RequireOriginals(pluginRoot,file);
            record=record with{Attempts=record.Attempts+1,RecordedAt=Now};AuthenticatedJsonFile.Write(journal,pluginKey,purpose,record);
            var prepared=new List<NationalTeamMigrationFile>();
            foreach(var file in record.Files)
            {
                token.ThrowIfCancellationRequested();prepared.Add(Prepare(pluginRoot,operation,file,pluginKey,record.Attempts));checkpoint?.Invoke("FilePrepared:"+file.Relative);token.ThrowIfCancellationRequested();
            }
            foreach(var file in record.Files)RequireOriginals(pluginRoot,file);
            record=record with{State="Prepared",Files=prepared,RecordedAt=Now};AuthenticatedJsonFile.Write(journal,pluginKey,purpose,record);checkpoint?.Invoke("Prepared");
        }
        Activate(pluginRoot,operation,record,token);VerifySnapshot(record);
        record=record with{State="Completed",RecordedAt=Now};AuthenticatedJsonFile.Write(journal,pluginKey,purpose,record);checkpoint?.Invoke("Completed");
        return new(operationId,"Completed",record.Files.Count(x=>x.Converted),record.Files);
    }
    private static NationalTeamMigrationFile Prepare(string root,string operation,NationalTeamMigrationFile file,byte[] key,int attempt)
    {
        if(!file.Present)return file;
        // 操作ID加已持久化尝试号唯一；避免再叠加长GUID路径触及原生SQLite路径上限。
        var source=PhysicalPathGuard.Child(root,file.Relative);var relative="prepared/"+attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)+"/"+Path.GetFileName(source);var target=PhysicalPathGuard.Child(operation,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if(IsPlain(source))
        {
            // 对已有普通库也只检查工作副本，防止只读SQLite触碰原有WAL/SHM。
            foreach(var suffix in Sidecars)if(File.Exists(source+suffix))File.Copy(PhysicalPathGuard.RejectLinks(source+suffix),target+suffix,false);
            return file with{Logical=LegacyNationalTeamExport.InspectPlain(target),PlainSha256=Hash(source)};
        }
        var logical=LegacyNationalTeamExport.Export(source,target,key);
        if(LegacyNationalTeamExport.InspectPlain(target)!=logical)throw new InvalidDataException("准备文件逻辑指纹不符。");
        return file with{Converted=true,PreparedRelative=relative,PlainSha256=Hash(target),Logical=logical};
    }
}
