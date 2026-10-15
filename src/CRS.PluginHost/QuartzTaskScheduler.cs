using Quartz;
using CRS.Plugin.Abstractions;
namespace CRS.PluginHost;

/// <summary>主程序统一拥有Quartz；计划与审计独立保存，回调只来自本次受信会话。</summary>
public sealed class QuartzTaskScheduler(string? storageRoot=null):IAsyncDisposable
{
    private sealed record Registration(Func<CancellationToken,Task> Work,CancellationToken Lifetime);
    private readonly SemaphoreSlim gate=new(1);
    private readonly CancellationTokenSource stop=new();
    private readonly Dictionary<JobKey,Registration> callbacks=[];
    private readonly Dictionary<JobKey,CancellationTokenSource> running=[];
    private readonly Dictionary<JobKey,TriggerKey> queued=[];
    private readonly Dictionary<JobKey,string> errors=[];
    private TaskPlanStore? store;
    private IScheduler? scheduler;
    private bool disposed;
    private TaskPlanStore Store=>store??=new(storageRoot);
    public IPluginScheduleGateway Scope(string pluginId,CancellationToken lifetime)=>new Scoped(this,pluginId,lifetime);
    private static JobKey Key(string pluginId,string taskId)=>new(taskId,pluginId);
    private static TriggerKey CronKey(JobKey key)=>new(key.Name+"-cron",key.Group);
    private static TriggerKey StartupKey(JobKey key)=>new(key.Name+"-startup",key.Group);
    private void EnsureActive(){ObjectDisposedException.ThrowIf(disposed,this);stop.Token.ThrowIfCancellationRequested();}
    private async Task EnsureSchedulerAsync(CancellationToken token)
    {
        if(scheduler is not null)return;
        var properties=new System.Collections.Specialized.NameValueCollection{{"quartz.scheduler.instanceName","CRS-"+Guid.NewGuid().ToString("N")}};
        scheduler=await QuartzSchedulerBuilder.Create().UseProperties(properties).BuildScheduler(token);
        scheduler.Context.Add("crs.owner",this);await scheduler.Start(token);
    }
    private async Task RegisterAsync(string pluginId,string taskId,string cron,string zone,Func<CancellationToken,Task> work,CancellationToken token,TimeSpan? delay,string? name,CancellationToken lifetime)
    {
        var defaults=new TaskPlan(pluginId,taskId,name??taskId,cron,zone,true);TaskPlanStore.ValidatePlan(defaults);
        if(delay is { } initial && (initial<TimeSpan.Zero || initial>TimeSpan.FromDays(1)))throw new ArgumentOutOfRangeException(nameof(delay));
        await gate.WaitAsync(token);
        try
        {
            EnsureActive();var key=Key(pluginId,taskId);
            if(callbacks.ContainsKey(key))throw new InvalidOperationException("该会话任务已经注册。");
            var plan=Store.AddDefault(defaults);await EnsureSchedulerAsync(token);
            callbacks[key]=new(work,lifetime);
            try
            {
                await scheduler!.AddJob(JobBuilder.Create<CallbackJob>().WithIdentity(key).StoreDurably().Build(),cancellationToken:token);
                await ApplyAsync(plan,delay,token);
            }
            catch{callbacks.Remove(key);await scheduler!.DeleteJob(key,CancellationToken.None);throw;}
        }
        finally{gate.Release();}
    }
    private async Task ApplyAsync(TaskPlan plan,TimeSpan? initialDelay,CancellationToken token)
    {
        var key=Key(plan.PluginId,plan.TaskId);
        await scheduler!.UnscheduleJob(CronKey(key),token);await scheduler.UnscheduleJob(StartupKey(key),token);
        if(!plan.Enabled)return;
        var cron=TriggerBuilder.Create().WithIdentity(CronKey(key)).ForJob(key)
            .WithCronSchedule(plan.Cron,x=>x.InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId)).WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing)).Build();
        await scheduler.ScheduleJob(cron,cancellationToken:token);
        if(initialDelay is { } delay)
        {
            // 初次同步也由Quartz驱动；暂停或编辑计划会移除此一次性触发器。
            var startup=TriggerBuilder.Create().WithIdentity(StartupKey(key)).ForJob(key).StartAt(DateTimeOffset.UtcNow+delay).Build();
            await scheduler.ScheduleJob(startup,cancellationToken:token);
        }
    }
    public async Task ConfigureAsync(string pluginId,string taskId,string cron,string zone,bool enabled,CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureActive();var key=Key(pluginId,taskId);var old=Store.Plans().Single(x=>x.PluginId==pluginId && x.TaskId==taskId);
            var plan=old with{Cron=cron,TimeZoneId=zone,Enabled=enabled};TaskPlanStore.ValidatePlan(plan);Store.Save(plan);
            if(!callbacks.ContainsKey(key))return;
            try{await ApplyAsync(plan,null,token);errors.Remove(key);}
            catch{errors[key]="PLAN_APPLY_FAILED";throw new InvalidOperationException("计划已保存，但调度应用失败，请重新加载状态或重启重试。");}
        }
        finally{gate.Release();}
    }
    public Task ConfigureSimpleAsync(string pluginId,string taskId,ScheduleFrequency frequency,int hour,int minute,bool enabled,CancellationToken token=default)
    {
        if(hour is <0 or >23 || minute is <0 or >59)throw new ArgumentException("小时应为0至23，分钟应为0至59。");
        var cron=frequency switch{ScheduleFrequency.Daily=>$"0 {minute} {hour} * * ?",ScheduleFrequency.Weekdays=>$"0 {minute} {hour} ? * MON-FRI",ScheduleFrequency.Hourly=>$"0 {minute} * * * ?",_=>throw new ArgumentException("请先选择每天、周一至周五或每小时。")};
        return ConfigureAsync(pluginId,taskId,cron,"China Standard Time",enabled,token);
    }
    public Task ConfigureSimpleTextAsync(string pluginId,string taskId,ScheduleFrequency frequency,string hour,string minute,bool enabled,CancellationToken token=default)
    {
        // 文本输入也在服务端校验，不能让WPF转换失败后悄悄保存上一次数值。
        if(!int.TryParse(hour,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var hours)
            || !int.TryParse(minute,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var minutes))throw new ArgumentException("小时和分钟必须填写有效整数。");
        return ConfigureSimpleAsync(pluginId,taskId,frequency,hours,minutes,enabled,token);
    }
    public async Task SetEnabledAsync(string pluginId,string taskId,bool enabled,CancellationToken token=default)
    {
        var plan=(await ListAsync(token)).Single(x=>x.Plan.PluginId==pluginId && x.Plan.TaskId==taskId).Plan;
        await ConfigureAsync(pluginId,taskId,plan.Cron,plan.TimeZoneId,enabled,token);
    }
    public async Task RunNowAsync(string pluginId,string taskId,CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureActive();var key=Key(pluginId,taskId);
            if(!callbacks.TryGetValue(key,out var registration) || registration.Lifetime.IsCancellationRequested)throw new InvalidOperationException("任务所属插件尚未启动。");
            if(running.ContainsKey(key) || queued.ContainsKey(key))throw new InvalidOperationException("该任务正在运行或排队，请等待或取消。");
            var triggerKey=new TriggerKey("manual-"+Guid.NewGuid().ToString("N"),pluginId);queued[key]=triggerKey;
            try{await scheduler!.ScheduleJob(TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(key).StartNow().Build(),cancellationToken:token);}
            catch{queued.Remove(key);throw;}
        }
        finally{gate.Release();}
    }
    public async Task CancelRunAsync(string pluginId,string taskId,CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureActive();var key=Key(pluginId,taskId);
            if(queued.Remove(key,out var trigger))await scheduler!.UnscheduleJob(trigger,token);
            if(running.TryGetValue(key,out var cancellation))cancellation.Cancel();
        }
        finally{gate.Release();}
    }
    public async Task<List<TaskPlanView>> ListAsync(CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureActive();var runs=Store.Runs();var result=new List<TaskPlanView>();
            foreach(var plan in Store.Plans())
            {
                var key=Key(plan.PluginId,plan.TaskId);DateTimeOffset? next=null;
                var registered=callbacks.TryGetValue(key,out var registration) && !registration.Lifetime.IsCancellationRequested;
                if(scheduler is not null && registered && !errors.ContainsKey(key))
                {
                    var cron=await scheduler.GetTrigger(CronKey(key),token);var initial=await scheduler.GetTrigger(StartupKey(key),token);
                    next=new[]{cron?.NextFireTimeUtc,initial?.NextFireTimeUtc}.Where(x=>x.HasValue).Min();
                }
                var (frequency,hour,minute)=plan.TimeZoneId=="China Standard Time"?Describe(plan.Cron):(ScheduleFrequency.Custom,0,0);
                result.Add(new(plan,registered,running.ContainsKey(key),queued.ContainsKey(key),next,runs.LastOrDefault(x=>x.PluginId==plan.PluginId && x.TaskId==plan.TaskId),frequency,hour,minute){ErrorCode=errors.GetValueOrDefault(key)});
            }
            return result;
        }
        finally{gate.Release();}
    }
    private static (ScheduleFrequency,int,int) Describe(string cron)
    {
        var parts=cron.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==6 && parts[0]=="0" && int.TryParse(parts[1],out var minute))
        {
            if(parts[2]=="*" && parts[3]=="*" && parts[4]=="*" && parts[5]=="?")return(ScheduleFrequency.Hourly,0,minute);
            if(int.TryParse(parts[2],out var hour) && parts[4]=="*")
            {
                if(parts[3]=="*" && parts[5]=="?")return(ScheduleFrequency.Daily,hour,minute);
                if(parts[3]=="?" && parts[5]=="MON-FRI")return(ScheduleFrequency.Weekdays,hour,minute);
            }
        }
        return(ScheduleFrequency.Custom,0,0);
    }
    public async Task<List<TaskRun>> HistoryAsync(CancellationToken token=default)
    {await gate.WaitAsync(token);try{EnsureActive();return Store.Runs().OrderByDescending(x=>x.StartedUtc).ToList();}finally{gate.Release();}}
    private async Task ExecuteAsync(IJobExecutionContext context,CancellationToken token)
    {
        var key=context.JobDetail.Key;Registration registration;CancellationTokenSource cancellation;TaskRun run;
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            if(disposed || stop.IsCancellationRequested || !callbacks.TryGetValue(key,out registration!))return;
            // 按完整触发器身份判断来源，不能把以manual-开头的任务名误认成手动执行。
            var origin=context.Trigger.Key==CronKey(key)?"Scheduled":context.Trigger.Key==StartupKey(key)?"Startup":"Manual";
            if(origin=="Manual")
            {
                // 被取消但已被Quartz取得的旧触发器也必须失效，不能移除后来新提交的请求。
                if(!queued.TryGetValue(key,out var expected) || expected!=context.Trigger.Key)return;
                queued.Remove(key);
            }
            var plan=Store.Plans().Single(x=>x.PluginId==key.Group && x.TaskId==key.Name);
            if(registration.Lifetime.IsCancellationRequested || (origin!="Manual" && (!plan.Enabled || errors.ContainsKey(key))))return;
            cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token,registration.Lifetime);
            run=new(Guid.NewGuid().ToString("N"),key.Group,key.Name,DateTimeOffset.UtcNow,null,TaskRunState.Running,origin,null);
            // 先持久记录再调用插件；写记录失败不能留下未记录的业务执行。
            try{Store.AddRun(run);running.Add(key,cancellation);}catch{errors[key]="HISTORY_WRITE_FAILED";cancellation.Dispose();throw;}
        }
        finally{gate.Release();}
        var state=TaskRunState.Succeeded;string? code=null;
        try{await registration.Work(cancellation.Token);cancellation.Token.ThrowIfCancellationRequested();}
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){state=TaskRunState.Cancelled;}
        catch(PluginTaskIncompleteException){state=TaskRunState.Incomplete;code="SOURCE_INCOMPLETE";}
        catch(Exception error){state=TaskRunState.Failed;code=error.GetType().Name;}
        finally
        {
            await gate.WaitAsync(CancellationToken.None);
            try{Store.Finish(run.Id,state,code);}
            catch{errors[key]="HISTORY_WRITE_FAILED";throw;}
            finally{running.Remove(key);cancellation.Dispose();gate.Release();}
        }
    }
    private sealed class Scoped(QuartzTaskScheduler owner,string id,CancellationToken lifetime):IPluginScheduleGateway
    {
        public Task RegisterAsync(string taskId,string cron,string zone,Func<CancellationToken,Task> work,CancellationToken token,TimeSpan? startupDelay=null,string? displayName=null)=>owner.RegisterAsync(id,taskId,cron,zone,work,token,startupDelay,displayName,lifetime);
    }
    [DisallowConcurrentExecution]
    public sealed class CallbackJob:IJob
    {
        public async ValueTask Execute(IJobExecutionContext context,CancellationToken cancellationToken=default)
        {await ((QuartzTaskScheduler)context.Scheduler.Context["crs.owner"]!).ExecuteAsync(context,cancellationToken);}
    }
    public void Cancel()=>stop.Cancel();
    public async ValueTask DisposeAsync()
    {
        Cancel();IScheduler? current;
        await gate.WaitAsync();try{if(disposed)return;disposed=true;current=scheduler;}finally{gate.Release();}
        // 不持有状态锁等待Quartz结束，执行中的任务还需要这个锁提交最终记录。
        if(current is not null)await current.Shutdown(waitForJobsToComplete:true);
        await gate.WaitAsync();try{callbacks.Clear();queued.Clear();store?.Dispose();}finally{gate.Release();}
    }
}
