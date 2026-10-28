using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Maintenance;
namespace CRS.Worker.Service;

/// <summary>由后台资料所有者识别旧日志与已完成升级的历史副本；不解密数据库或服务密钥，不清理原始证据。</summary>
public sealed partial class WorkerDataCleanup:IAsyncDisposable
{
    private const int MaximumFiles=500000;
    private readonly string root;
    private readonly TimeProvider clock;
    private readonly object stateGate=new();
    private readonly Dictionary<Guid,Operation> operations=[];
    private readonly CancellationTokenSource stopping=new();
    private Operation? active;
    private sealed class Operation(Guid id,string sid,DataCleanupOptions options)
    {
        public Guid Id=id;public string Sid=sid;public DataCleanupOptions Options=options;
        public DataCleanupStatus Status=new(id,"Scanning",CRS.Timing.BeijingClock.Now,null,[],0,0,"正在识别资料用途。");
        public List<Target> Targets=[];public List<Target> Retained=[];public string? CurrentJournalHash;public Task? Task;
    }
    private sealed record Target(DataCleanupEntry Entry,string FullPath,string? ArchiveHash,string? ManifestHash,
        Dictionary<string,string>? ExpectedHashes,long Length=0,long ModifiedTicks=0);
    private sealed record SnapshotManifest(int Format,string ReleaseHash,Dictionary<string,string> Files);
    private static readonly JsonSerializerOptions Json=new(){RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true};
    public WorkerDataCleanup(string dataDirectory,TimeProvider? timeProvider=null)
    {root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));clock=timeProvider??TimeProvider.System;StorageSecurity.RejectLinks(root);}
    private DateTimeOffset Now=>CRS.Timing.BeijingClock.Read(clock);
    private string Child(string relative)
    {
        if(Path.IsPathRooted(relative)||relative.Contains(':')||relative.Split('/','\\').Any(x=>x is "" or "." or ".."))throw new InvalidDataException("清理路径无效。");
        var path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("清理路径越出后台资料目录。");
        StorageSecurity.RejectLinks(path);return path;
    }
    private static void Validate(DataCleanupOptions options)
    {if(options.LogRetentionDays is <1 or >365||options.HistoricalBackupsToKeep is <2 or >20)throw new InvalidDataException("日志至少保留1天，历史升级备份至少保留2份。");}
    /// <summary>只读审计入口，用于隔离验证和现场盘点；不会写预览、创建目录或获取维护锁。</summary>
    public DataCleanupStatus Audit(DataCleanupOptions options,CancellationToken token=default)
    {
        Validate(options);var operation=new Operation(Guid.NewGuid(),"audit",options);Scan(operation,token);return operation.Status;
    }
    private void Scan(Operation operation,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(root);var entries=new List<DataCleanupEntry>();var targets=new List<Target>();
        var logs=Child("logs");
        if(Directory.Exists(logs))foreach(var file in new DirectoryInfo(logs).EnumerateFiles())
        {
            token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(file.FullName);
            var match=System.Text.RegularExpressions.Regex.Match(file.Name,@"^service-([0-9]{8})\.jsonl$",System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var eligible=match.Success&&DateOnly.TryParseExact(match.Groups[1].Value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)
                &&day<DateOnly.FromDateTime(Now.DateTime).AddDays(-operation.Options.LogRetentionDays)&&file.LastWriteTimeUtc<Now.UtcDateTime.AddDays(-operation.Options.LogRetentionDays);
            var entry=new DataCleanupEntry("log:"+file.Name,"Logs",file.Name,Path.GetRelativePath(root,file.FullName),file.Length,1,eligible,eligible,eligible?"已超过日志保留期；保留今天和保留期内日志。":"当前日志、保留期内日志或未知文件，保留。");
            entries.Add(entry);if(eligible)targets.Add(new(entry,file.FullName,null,null,null,file.Length,file.LastWriteTimeUtc.Ticks));
        }
        var maintenance=Child("maintenance");var journal=Path.Combine(maintenance,"upgrade.json");WorkerUpgradeRecord? current=null;string? currentHash=null;
        if(File.Exists(journal)){StorageSecurity.RejectLinks(journal);current=new WorkerUpgradeJournal(root).Read();currentHash=Hash(journal);}
        operation.CurrentJournalHash=currentHash;
        var backups=new List<(WorkerUpgradeRecord Record,string Archive,Target Target)>();
        if(Directory.Exists(maintenance))foreach(var directory in new DirectoryInfo(maintenance).EnumerateDirectories())
        {
            token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(directory.FullName);
            if(directory.Name is "cleanup" or "cleanup-trash")continue;
            if(!Guid.TryParseExact(directory.Name,"N",out _))continue;
            var relative=Path.GetRelativePath(root,directory.FullName);var entry=new DataCleanupEntry(directory.Name,"UpgradeBackup","历史升级备份 "+directory.Name[..8],relative,null,null,false,false,"未能核实历史维护身份，保留。");
            try
            {
                var archive=Child("maintenance/"+directory.Name+".json");
                // 当前备份没有历史归档也必须保留；只读取当前维护状态，不把它列为清理候选。
                if(current?.Id==directory.Name){entries.Add(entry with{Reason="当前维护事务引用的回退快照，必须保留。"});continue;}
                if(!File.Exists(archive)){entries.Add(entry);continue;}
                var record=ReadRecord(archive);
                if(record.Id!=directory.Name||record.Stage is not ("Committed" or "RolledBack")||record.BackupManifestHash is null||record.UpdatedUtc>Now)
                {entries.Add(entry with{Reason="维护未完成、时间异常或缺少完整备份证明，保留。"});continue;}
                var target=InspectSnapshot(entry,directory.FullName,record,Hash(archive),token);
                backups.Add((record,archive,target));
            }
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException)
            {entries.Add(entry with{Reason="备份身份、路径或文件清单无法核实，保留。"});}
        }
        var retained=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var damaged=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var backup in backups.OrderByDescending(x=>x.Record.UpdatedUtc))
        {
            if(retained.Count>=operation.Options.HistoricalBackupsToKeep)break;
            try{VerifySnapshot(backup.Target,false,token);retained.Add(backup.Record.Id);}
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException){damaged.Add(backup.Record.Id);}
        }
        foreach(var backup in backups.OrderByDescending(x=>x.Record.UpdatedUtc))
        {
            var eligible=current is not null&&current.UpdatedUtc<=Now&&(current.Stage is "Committed" or "RolledBack")&&!retained.Contains(backup.Record.Id)&&!damaged.Contains(backup.Record.Id);
            var entry=backup.Target.Entry with{Eligible=eligible,Selected=eligible&&operation.Options.IncludeHistoricalBackups,
                Reason=eligible?"已完成的旧升级副本；清理后不能依靠此副本回退到该历史版本。":retained.Contains(backup.Record.Id)?"保留最近两份或所选数量的已验证历史回退副本。":damaged.Contains(backup.Record.Id)?"备份字节与清单不符，保留现场供管理员处理。":"当前维护状态未确认完成，保留全部升级副本。"};
            entries.Add(entry);if(entry.Selected)targets.Add(backup.Target with{Entry=entry});
        }
        AddRemainders(entries,targets,current,token);
        entries.Add(new("live","Protected","正在使用的数据库、密钥和插件原件",".",null,null,false,false,"scheduler-config.db、source-budgets.db、WAL/SHM、service-key.dpapi、owner.lock、plugins内data/raw均不属于本功能清理范围。"));
        entries.Add(new("journal","Protected","维护日志与未识别资料","maintenance",null,null,false,false,"保留upgrade.json、历史维护记录、未知目录和无法核实的文件；不按大小或扩展名猜测用途。"));
        if(entries.Count>128)throw new InvalidDataException("清理预览超过128项，请先由管理员检查异常增长。");
        lock(stateGate){operation.Targets=targets;operation.Retained=backups.Where(x=>retained.Contains(x.Record.Id)).Select(x=>x.Target).ToList();operation.Status=new(operation.Id,"Ready",Now,Now.AddMinutes(15),entries.AsReadOnly(),0,0,"预览完成。仅选中项会清理，当前数据和最近历史备份保留。",targets.Count>0);}
    }
    private Target InspectSnapshot(DataCleanupEntry entry,string directory,WorkerUpgradeRecord record,string archiveHash,CancellationToken token,bool allowMissing=false)
    {
        var manifestPath=Path.Combine(directory,"snapshot.json");StorageSecurity.RejectLinks(manifestPath);
        if(new FileInfo(manifestPath).Length>32*1024*1024||!Hash(manifestPath).Equals(record.BackupManifestHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("升级快照清单摘要不符。");
        var manifest=JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllBytes(manifestPath),Json)??throw new InvalidDataException("备份清单缺失。");
        if(manifest.Format!=1||manifest.ReleaseHash!=record.PreviousReleaseHash||manifest.Files.Count is <2 or >100000||!manifest.Files.ContainsKey("service-key.dpapi")||!manifest.Files.ContainsKey("scheduler-config.db"))throw new InvalidDataException("备份清单不完整。");
        var expected=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){["snapshot.json"]=record.BackupManifestHash!};
        foreach(var pair in manifest.Files)
        {
            if(Path.IsPathRooted(pair.Key)||pair.Key.Contains(':')||pair.Key.Split('/','\\').Any(x=>x is "" or "." or "..")||pair.Value.Length!=64||!pair.Value.All(char.IsAsciiHexDigit))throw new InvalidDataException("备份清单路径或摘要无效。");
            if(!expected.TryAdd("snapshot/"+pair.Key.Replace('\\','/'),pair.Value))throw new InvalidDataException("备份清单重复。");
        }
        long bytes=0;var files=Files(directory,token);
        if(!allowMissing&&files.Count!=expected.Count)throw new InvalidDataException("备份文件数与清单不一致。");
        foreach(var file in files){if(!expected.ContainsKey(Path.GetRelativePath(directory,file.FullName).Replace('\\','/')))throw new InvalidDataException("备份含未登记文件。");bytes=checked(bytes+file.Length);}
        return new(entry with{Bytes=bytes,Files=files.Count},directory,archiveHash,record.BackupManifestHash,expected);
    }
    private List<FileInfo> Files(string directory,CancellationToken token)
    {
        if(!Directory.Exists(directory))return [];
        StorageSecurity.RejectLinks(directory);
        var files=new List<FileInfo>();var pending=new Stack<DirectoryInfo>();pending.Push(new(directory));
        while(pending.TryPop(out var parent))foreach(var entry in parent.EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();StorageSecurity.RejectLinks(entry.FullName);
            if(entry is DirectoryInfo child)pending.Push(child);else files.Add((FileInfo)entry);
            if(files.Count>MaximumFiles)throw new InvalidDataException("清理目录超过安全枚举限额。");
        }
        return files;
    }
    private static WorkerUpgradeRecord ReadRecord(string path)
    {
        if(new FileInfo(path).Length>16384)throw new InvalidDataException("维护记录超限。");
        var record=JsonSerializer.Deserialize<WorkerUpgradeRecord>(File.ReadAllBytes(path),Json)??throw new InvalidDataException("维护记录为空。");
        static bool Digest(string value)=>value.Length==64&&value.All(char.IsAsciiHexDigit);
        if(record.Format!=1||!Guid.TryParseExact(record.Id,"N",out _)||!Digest(record.PreviousReleaseHash)||!Digest(record.CandidateReleaseHash)
            ||!Version.TryParse(record.PreviousVersion,out _)||!Version.TryParse(record.CandidateVersion,out _)
            ||record.Stage is not ("Committed" or "RolledBack")||record.Stage=="Committed"&&!record.NewFactsPossible||record.Stage=="RolledBack"&&record.NewFactsPossible)
            throw new InvalidDataException("维护记录格式无效。");return record;
    }
    private static string Hash(string path,CancellationToken token=default)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[81920];int read;
        while((read=input.Read(buffer))>0){token.ThrowIfCancellationRequested();hash.AppendData(buffer,0,read);}token.ThrowIfCancellationRequested();return Convert.ToHexString(hash.GetHashAndReset());
    }
}
