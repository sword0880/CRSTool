using System.IO;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.Maintenance;
using CRS.DesktopClient.Services;
using CRS.Worker.Ipc;
using System.Text.Json;

internal static class CleanupTaskModelChecks
{
    // 显式只读诊断：通过现有管理员协议验证已安装后台的清理摘要与桌面合并展示，不提交或确认任务。
    internal static async Task ProbeAsync()
    {
        var gateway=new WorkerDataCleanupGateway();
        if(!gateway.CanExecute)throw new InvalidOperationException("读取后台私有清理摘要需要管理员权限。");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cleanup=new DataCleanupViewModel(gateway);await cleanup.RefreshTaskCommand.ExecuteAsync(null);
        if(!cleanup.TaskStatusKnown)throw new InvalidOperationException(cleanup.TaskMessage);
        var client=new BackgroundPipeClient();var health=await client.QueryAsync("GetHealth",deadline.Token);
        var activity=await WorkerTaskActivityReader.ReadAsync(client,deadline.Token);
        var status=ShellStatusBarViewModel.Describe(health,cleanup.CurrentTask,activity:activity);
        var center=new BackgroundServiceViewModel(deadline.Token,client,cleanup);await center.RefreshTaskActivityAsync();
        if(!center.ActivityStatus.Contains("北京时间"))throw new InvalidOperationException(center.ActivityStatus);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ObservedAt=CRS.Timing.BeijingClock.Now,CurrentId=cleanup.CurrentTask?.Id,CurrentState=cleanup.CurrentTask?.State,
            status.TaskLabel,status.TaskDetail,center.ActivityStatus,
            CleanupRows=center.RunningTasks.Concat(center.WaitingTasks).Where(x=>x.Row.JobId=="data-cleanup").Select(x=>new{x.Row.State,x.ReasonText,x.Row.RequestId})
        }));
    }
    internal sealed class Gateway:IDataCleanupGateway,IDataCleanupTaskGateway
    {
        public bool CanExecute=>true;
        public DataCleanupTaskOverview Overview=new(null,null,null,true);
        public List<Guid> Requests=[];public int Created,Acknowledged;public bool DropNextReply,FailRead;
        public Task<DataCleanupTaskOverview> SubmitAsync(Guid id,CancellationToken token)
        {
            Requests.Add(id);
            if(Overview.Current is null){Created++;Overview=new(new(id,"Manual","Queued",CRS.Timing.BeijingClock.Now,CRS.Timing.BeijingClock.Now,null,0,0,true,"已受理"),Overview.LatestResult,Overview.Notification,false);}
            if(DropNextReply){DropNextReply=false;throw new IOException("合成响应中断");}
            return Task.FromResult(Overview);
        }
        public Task<DataCleanupTaskOverview> LatestAsync(CancellationToken token)=>FailRead?throw new IOException("合成查询中断"):Task.FromResult(Overview);
        public Task<DataCleanupTaskOverview> AcknowledgeAsync(Guid id,CancellationToken token)
        {if(Overview.Notification?.Id!=id)throw new Exception("确认了其他通知。");Acknowledged++;Overview=Overview with{Notification=null};return Task.FromResult(Overview);}
        public void Complete(bool known=true,string state="Completed")
        {
            var current=Overview.Current??new DataCleanupTask(Guid.NewGuid(),"Automatic","Scanning",CRS.Timing.BeijingClock.Now,CRS.Timing.BeijingClock.Now,null,0,0,true,"");
            var result=current with{State=state,UpdatedAt=CRS.Timing.BeijingClock.Now,FinishedAt=CRS.Timing.BeijingClock.Now,DeletedBytes=32*1024*1024,DeletedFiles=17,CountsComplete=known,Message="合成后台结果"};
            Overview=new(null,result,result,true);
        }
        public Task<DataCleanupStatus> PreviewAsync(DataCleanupOptions options,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>throw new Exception("新界面不能调用旧扫描等待流程。");
        public Task<DataCleanupStatus> ExecuteAsync(Guid previewId,Guid executionId,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>throw new Exception("新界面不能调用预览清理流程。");
        public Task<DataCleanupStatus> StatusAsync(Guid id,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>throw new Exception("新界面不维持旧操作轮询。");
    }
    internal static async Task RunAsync()
    {
        var passed=0;void Check(bool value,string text){if(!value)throw new Exception(text);passed++;Console.WriteLine("PASS 前台后台清理任务："+text);}
        var gateway=new Gateway();var model=new DataCleanupViewModel(gateway);
        await model.SubmitTaskCommand.ExecuteAsync(null);
        Check(!model.TaskRequestBusy&&!model.Busy&&gateway.Created==1&&model.TaskMessage.Contains("关闭设置"),"提交后立即返回，前台不等待完整清理");
        Check(model.TaskStatusKnown&&model.CurrentTask?.State=="Queued","受理响应立即保留当前排队任务供任务中心和底栏显示");
        Check(model.AutomaticPlanStatus.Contains("未提供"),"旧后台缺少维护计划时明确未确认，不编造下次时间");
        gateway.Overview=gateway.Overview with{AutomaticPlan=new("Waiting",CRS.Timing.BeijingClock.Now.AddDays(1),300,30,86400,3600,14,1)};
        await model.RefreshTaskCommand.ExecuteAsync(null);
        var legacyPlan=new ServicePlanItem(model);
        Check(model.AutomaticPlanPolicy.Contains("24")&&model.AutomaticPlanStatus.Contains("已启用")&&legacyPlan.Cron=="—"
            &&legacyPlan.NextDueAt==gateway.Overview.AutomaticPlan.NextDueAt,"从服务摘要展示真实保留规则及到期时间");
        model.StopWaiting();Check(gateway.Overview.Current is not null,"关闭设置的等待清理不取消后台任务");
        await model.SubmitTaskCommand.ExecuteAsync(null);Check(gateway.Created==1,"重复点击由后台复用已存在任务");
        gateway.Complete();var restarted=new DataCleanupViewModel(gateway);await restarted.RefreshTaskCommand.ExecuteAsync(null);
        Check(restarted.HasTaskNotification&&restarted.TaskNotificationMessage.Contains("17"),"新建前台会话无需原任务编号即可取得完成提醒");
        await restarted.DismissTaskNotificationCommand.ExecuteAsync(null);
        Check(!restarted.HasTaskNotification&&gateway.Acknowledged==1,"确认完成提醒后持久标记已读");
        Check(restarted.TaskStatusKnown&&restarted.CurrentTask is null,"完成摘要撤回当前任务，同时保留完成提醒的既有流程");
        var again=new DataCleanupViewModel(gateway);await again.RefreshTaskCommand.ExecuteAsync(null);Check(!again.HasTaskNotification,"再次打开前台不重复提醒已读结果");
        gateway.Complete(false,"Interrupted");await again.RefreshTaskCommand.ExecuteAsync(null);
        Check(again.TaskNotificationTitle.Contains("未全部完成")&&again.TaskNotificationMessage.Contains("未完整记录"),"中断与未知数量不误报清理成功或零删除");
        var uncertain=new Gateway{DropNextReply=true};var retry=new DataCleanupViewModel(uncertain);
        await retry.SubmitTaskCommand.ExecuteAsync(null);await retry.SubmitTaskCommand.ExecuteAsync(null);
        Check(uncertain.Created==1&&uncertain.Requests.Count==2&&uncertain.Requests.Distinct().Count()==1,"响应丢失后保留原提交编号，重试不创建第二轮任务");
        Check(!new DataCleanupViewModel(null).SubmitTaskCommand.CanExecute(null),"没有任务入口时不提供提交能力");
        uncertain.FailRead=true;await retry.RefreshTaskCommand.ExecuteAsync(null);
        Check(!retry.TaskStatusKnown&&retry.CurrentTask is not null,"摘要查询失败保留旧任务并标记未确认，不误报清理结束");
        uncertain.FailRead=false;await retry.RefreshTaskCommand.ExecuteAsync(null);
        Check(retry.TaskStatusKnown&&retry.CurrentTask?.State=="Queued","查询恢复后重新确认现有任务，不发起清理");
        Console.WriteLine($"前台后台清理任务模型验证通过：{passed}项。");
    }
}
