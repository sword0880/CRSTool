using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
namespace CRS.PluginHost;
public enum UpgradeStage { Prepared, Snapshotted, Migrated, Switched, Committed, RolledBack, RequiresRecovery }
public sealed record ActiveGeneration(string PackageVersion,string DbGeneration);
public sealed record UpgradeRecord(string Id,ActiveGeneration Previous,ActiveGeneration Candidate,UpgradeStage Stage,bool NewFactsWritten);
// 调用升级协调器前，必须冻结插件写入并验证官方包。
// 数据库快照、迁移和原件引用由插件负责，不借用税务基础设施。
public interface IPluginUpgradeParticipant
{
    Task SnapshotAsync(ActiveGeneration previous,CancellationToken cancellationToken);
    Task MigrateCandidateAsync(ActiveGeneration candidate,CancellationToken cancellationToken);
    Task VerifyCandidateAsync(ActiveGeneration candidate,CancellationToken cancellationToken);
}
/// <summary>协调跨文件升级的恢复状态；存在新事实时禁止静默回退旧快照。</summary>
public sealed class UpgradeCoordinator(string directory,Action<string>? checkpoint=null)
{
    // 同一进程内的后台写入和启动确认共用锁，避免覆盖新事实标记或争用临时文件。
    private static readonly ConcurrentDictionary<string,object> Gates=new(StringComparer.OrdinalIgnoreCase);
    private static object Gate(string path)=>Gates.GetOrAdd(Path.GetFullPath(path),_=>new object());
    private string JournalPath=>Path.Combine(directory,"upgrade-journal.json");
    private string ActivePath=>Path.Combine(directory,"active-version.json");
    private static void Write<T>(string path,T value)
    {
        lock(Gate(path))
        {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+".tmp";
        using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        { JsonSerializer.Serialize(output,value);output.Flush(true); }
        File.Move(temp,path,true);
        }
    }
    private static ActiveGeneration Validate(ActiveGeneration value)
    {
        if(value is null || !Version.TryParse(value.PackageVersion,out _) || string.IsNullOrWhiteSpace(value.DbGeneration) || value.DbGeneration.Length>64 || !value.DbGeneration.All(x=>char.IsAsciiLetterOrDigit(x) || x=='-'))throw new InvalidDataException("升级版本或数据库代次无效。");
        return value;
    }
    public UpgradeRecord? Read()
    {
        lock(Gate(JournalPath))
        {
        if(!File.Exists(JournalPath))return null;
        var record=JsonSerializer.Deserialize<UpgradeRecord>(File.ReadAllText(JournalPath))??throw new InvalidDataException("升级日志损坏。");
        if(!Enum.IsDefined(record.Stage) || !Guid.TryParse(record.Id,out _))throw new InvalidDataException("升级日志状态或标识无效。");
        Validate(record.Previous);Validate(record.Candidate);return record;
        }
    }
    public ActiveGeneration? Active()=>File.Exists(ActivePath)?Validate(JsonSerializer.Deserialize<ActiveGeneration>(File.ReadAllText(ActivePath))??throw new InvalidDataException("当前数据版本指针损坏。")):null;
    public async Task PrepareAsync(ActiveGeneration previous,ActiveGeneration candidate,IPluginUpgradeParticipant participant,CancellationToken cancellationToken)
    {
        Validate(previous);Validate(candidate);
        if(Read() is {Stage:not (UpgradeStage.Committed or UpgradeStage.RolledBack)}) throw new InvalidOperationException("Unresolved upgrade journal.");
        var record=new UpgradeRecord(Guid.NewGuid().ToString("N"),previous,candidate,UpgradeStage.Prepared,false);Write(JournalPath,record);
        checkpoint?.Invoke("Prepared");
        await participant.SnapshotAsync(previous,cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke("SnapshotWritten");
        record=record with {Stage=UpgradeStage.Snapshotted};Write(JournalPath,record);
        checkpoint?.Invoke("Snapshotted");
        await participant.MigrateCandidateAsync(candidate,cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke("CandidateMigrated");
        await participant.VerifyCandidateAsync(candidate,cancellationToken).ConfigureAwait(false);
        record=record with {Stage=UpgradeStage.Migrated};Write(JournalPath,record);
        checkpoint?.Invoke("Migrated");
        // 先记日志再切指针；两次写入之间崩溃时，恢复能够识别任一指针状态。
        record=record with {Stage=UpgradeStage.Switched};Write(JournalPath,record);checkpoint?.Invoke("SwitchRecorded");Write(ActivePath,candidate);checkpoint?.Invoke("PointerSwitched");
    }
    public void MarkBeforeNewFactWrite()
    {
        lock(Gate(JournalPath))
        {
        var record=Read()??throw new InvalidOperationException("No upgrade journal.");
        if(record.Stage!=UpgradeStage.Switched) throw new InvalidOperationException("Candidate has not started.");
        Write(JournalPath,record with {NewFactsWritten=true});
        }
    }
    public async Task<UpgradeStage> RecoverAsync(IPluginUpgradeParticipant participant,bool confirmCandidate,CancellationToken cancellationToken)
    {
        var record=Read();if(record is null) throw new InvalidOperationException("No upgrade journal.");
        if(record.Stage is UpgradeStage.Committed or UpgradeStage.RolledBack or UpgradeStage.RequiresRecovery) return record.Stage;
        if(record.Stage==UpgradeStage.Switched && confirmCandidate)
        {
            try
            {
                await participant.VerifyCandidateAsync(record.Candidate,cancellationToken).ConfigureAwait(false);
                lock(Gate(JournalPath))
                {
                    record=Read()!;
                    Write(ActivePath,record.Candidate);Write(JournalPath,record with {Stage=UpgradeStage.Committed});return UpgradeStage.Committed;
                }
            }
            catch(OperationCanceledException) {throw;}
            catch(Exception) { /* 保留候选数据，按下方的新事实写入标记决定恢复方式。 */ }
        }
        lock(Gate(JournalPath))
        {
            // 核验等待期间会话可能提交新事实，恢复必须依据最新日志作出决定。
            record=Read()!;
            if(record.NewFactsWritten) {Write(JournalPath,record with {Stage=UpgradeStage.RequiresRecovery});return UpgradeStage.RequiresRecovery;}
            Write(ActivePath,record.Previous);Write(JournalPath,record with {Stage=UpgradeStage.RolledBack});return UpgradeStage.RolledBack;
        }
    }
}
