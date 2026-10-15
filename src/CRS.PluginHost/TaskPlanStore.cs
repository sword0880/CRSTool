using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quartz;

namespace CRS.PluginHost;

public enum TaskRunState { Running, Succeeded, Failed, Cancelled, Interrupted, Incomplete, Skipped }
public enum ScheduleFrequency { Daily, Weekdays, Hourly, Custom }
public sealed record TaskPlan(string PluginId,string TaskId,string Name,string Cron,string TimeZoneId,bool Enabled);
public sealed record TaskRun(string Id,string PluginId,string TaskId,DateTimeOffset StartedUtc,DateTimeOffset? EndedUtc,TaskRunState State,string Origin,string? FailureCode);
public sealed record TaskPlanView(TaskPlan Plan,bool Registered,bool Running,bool Queued,DateTimeOffset? NextUtc,TaskRun? LastRun,ScheduleFrequency Frequency,int Hour,int Minute)
{public string? ErrorCode {get;init;}}
internal sealed record TaskPlanDocument(int Version,List<TaskPlan> Plans,List<TaskRun> Runs);

/// <summary>任务元数据独立保存，不含回调、认证材料、请求地址或业务金额；文件锁防止双宿主覆盖。</summary>
internal sealed class TaskPlanStore:IDisposable
{
    private readonly object sync=new();
    private readonly string? file;
    private readonly FileStream? lease;
    private TaskPlanDocument document=new(1,[],[]);
    private static readonly JsonSerializerOptions Json=new(){Converters={new JsonStringEnumConverter()}};
    public TaskPlanStore(string? root)
    {
        if(root is null)return;
        var directory=PackageVerifier.Resolve(root,"scheduled-tasks");Directory.CreateDirectory(directory);file=Path.Combine(directory,"tasks.json");
        lease=new FileStream(Path.Combine(directory,"owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        try
        {
            if(File.Exists(file))
            {
                if(new FileInfo(file).Length>2*1024*1024)throw new InvalidDataException("计划任务文件超过上限。");
                document=JsonSerializer.Deserialize<TaskPlanDocument>(File.ReadAllBytes(file),Json)??throw new InvalidDataException("计划任务文件损坏。");
                Validate(document);
                // 获取独占所有权后，上一宿主遗留的运行中记录只能标为中断，不能冒充成功。
                if(document.Runs.Any(x=>x.State==TaskRunState.Running))
                    Commit(document with{Runs=document.Runs.Select(x=>x.State==TaskRunState.Running?x with{State=TaskRunState.Interrupted,EndedUtc=DateTimeOffset.UtcNow,FailureCode="HOST_EXITED"}:x).ToList()});
            }
        }
        catch{lease.Dispose();throw;}
    }
    internal static void ValidatePlan(TaskPlan plan)
    {
        if(plan is null || string.IsNullOrWhiteSpace(plan.PluginId) || plan.PluginId.Length>100 || !plan.PluginId.All(x=>char.IsAsciiLetterOrDigit(x) || x is '-' or '.')
            || string.IsNullOrWhiteSpace(plan.TaskId) || plan.TaskId.Length>80 || !plan.TaskId.All(x=>char.IsAsciiLetterOrDigit(x) || x=='-')
            || string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length>120 || plan.Cron is null || plan.Cron.Length>200 || plan.TimeZoneId is null || plan.TimeZoneId.Length>100)
            throw new InvalidDataException("计划任务配置无效。");
        _=new CronExpression(plan.Cron);_=TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId);
    }
    private static void Validate(TaskPlanDocument value)
    {
        if(value.Version!=1 || value.Plans is null || value.Runs is null || value.Plans.Count>1000 || value.Runs.Count>500)throw new InvalidDataException("不支持或损坏的计划任务文件。");
        foreach(var plan in value.Plans)ValidatePlan(plan);
        if(value.Plans.GroupBy(x=>(x.PluginId,x.TaskId)).Any(x=>x.Count()>1) || value.Runs.Any(x=>x is null || !Guid.TryParse(x.Id,out _) || !Enum.IsDefined(x.State)
            || !value.Plans.Any(p=>p.PluginId==x.PluginId && p.TaskId==x.TaskId) || x.Origin is not ("Scheduled" or "Startup" or "Manual")
            || x.FailureCode?.Length>100 || (x.State==TaskRunState.Running)!=(x.EndedUtc is null)))throw new InvalidDataException("计划任务记录无效。");
    }
    private void Commit(TaskPlanDocument value)
    {
        Validate(value);
        if(file is not null)
        {
            var temporary=file+".tmp";
            using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(output,value,Json);output.Flush(true);}
            File.Move(temporary,file,true);
        }
        document=value;
    }
    public List<TaskPlan> Plans(){lock(sync)return [..document.Plans];}
    public List<TaskRun> Runs(){lock(sync)return [..document.Runs];}
    public TaskPlan AddDefault(TaskPlan plan)
    {
        lock(sync)
        {
            var existing=document.Plans.SingleOrDefault(x=>x.PluginId==plan.PluginId && x.TaskId==plan.TaskId);
            if(existing is not null)return existing;
            Commit(document with{Plans=[..document.Plans,plan]});return plan;
        }
    }
    public void Save(TaskPlan plan){lock(sync){if(!document.Plans.Any(x=>x.PluginId==plan.PluginId && x.TaskId==plan.TaskId))throw new InvalidDataException("任务不存在。");Commit(document with{Plans=document.Plans.Select(x=>x.PluginId==plan.PluginId && x.TaskId==plan.TaskId?plan:x).ToList()});}}
    public void AddRun(TaskRun run)
    {
        lock(sync)
        {
            // 保留所有尚未完成的记录，不能因短任务频繁完成而挤掉长任务的开始记录。
            var active=document.Runs.Where(x=>x.State==TaskRunState.Running).ToList();
            if(active.Count>=500)throw new InvalidOperationException("同时运行任务的记录达到上限。");
            var completed=document.Runs.Where(x=>x.State!=TaskRunState.Running).TakeLast(499-active.Count);
            Commit(document with{Runs=completed.Concat(active).Append(run).OrderBy(x=>x.StartedUtc).ToList()});
        }
    }
    public void Finish(string id,TaskRunState state,string? code)
    {lock(sync)Commit(document with{Runs=document.Runs.Select(x=>x.Id==id?x with{State=state,EndedUtc=DateTimeOffset.UtcNow,FailureCode=code}:x).ToList()});}
    public void Dispose()=>lease?.Dispose();
}
