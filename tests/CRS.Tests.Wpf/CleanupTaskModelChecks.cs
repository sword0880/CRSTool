using System.IO;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.Maintenance;

internal static class CleanupTaskModelChecks
{
    internal sealed class Gateway:IDataCleanupGateway,IDataCleanupTaskGateway
    {
        public bool CanExecute=>true;
        public DataCleanupTaskOverview Overview=new(null,null,null,true);
        public List<Guid> Requests=[];public int Created,Acknowledged;public bool DropNextReply;
        public Task<DataCleanupTaskOverview> SubmitAsync(Guid id,CancellationToken token)
        {
            Requests.Add(id);
            if(Overview.Current is null){Created++;Overview=new(new(id,"Manual","Queued",CRS.Timing.BeijingClock.Now,CRS.Timing.BeijingClock.Now,null,0,0,true,"已受理"),Overview.LatestResult,Overview.Notification,false);}
            if(DropNextReply){DropNextReply=false;throw new IOException("合成响应中断");}
            return Task.FromResult(Overview);
        }
        public Task<DataCleanupTaskOverview> LatestAsync(CancellationToken token)=>Task.FromResult(Overview);
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
        model.StopWaiting();Check(gateway.Overview.Current is not null,"关闭设置的等待清理不取消后台任务");
        await model.SubmitTaskCommand.ExecuteAsync(null);Check(gateway.Created==1,"重复点击由后台复用已存在任务");
        gateway.Complete();var restarted=new DataCleanupViewModel(gateway);await restarted.RefreshTaskCommand.ExecuteAsync(null);
        Check(restarted.HasTaskNotification&&restarted.TaskNotificationMessage.Contains("17"),"新建前台会话无需原任务编号即可取得完成提醒");
        await restarted.DismissTaskNotificationCommand.ExecuteAsync(null);
        Check(!restarted.HasTaskNotification&&gateway.Acknowledged==1,"确认完成提醒后持久标记已读");
        var again=new DataCleanupViewModel(gateway);await again.RefreshTaskCommand.ExecuteAsync(null);Check(!again.HasTaskNotification,"再次打开前台不重复提醒已读结果");
        gateway.Complete(false,"Interrupted");await again.RefreshTaskCommand.ExecuteAsync(null);
        Check(again.TaskNotificationTitle.Contains("未全部完成")&&again.TaskNotificationMessage.Contains("未完整记录"),"中断与未知数量不误报清理成功或零删除");
        var uncertain=new Gateway{DropNextReply=true};var retry=new DataCleanupViewModel(uncertain);
        await retry.SubmitTaskCommand.ExecuteAsync(null);await retry.SubmitTaskCommand.ExecuteAsync(null);
        Check(uncertain.Created==1&&uncertain.Requests.Count==2&&uncertain.Requests.Distinct().Count()==1,"响应丢失后保留原提交编号，重试不创建第二轮任务");
        Check(!new DataCleanupViewModel(null).SubmitTaskCommand.CanExecute(null),"没有任务入口时不提供提交能力");
        Console.WriteLine($"前台后台清理任务模型验证通过：{passed}项。");
    }
}
