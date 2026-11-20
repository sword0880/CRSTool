using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.PluginHost;

internal static class ScheduleEditChecks
{
    private static void Check(bool value,string text)
    {if(!value)throw new Exception(text);Console.WriteLine("PASS 任务详情启停："+text);}
    internal static async Task RunAsync()
    {
        var control=new Control();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pipe="CRS.ScheduleEdit."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var parent=new BackgroundServiceViewModel(stop.Token,new(pipe));await parent.RefreshCommand.ExecuteAsync(null);
            var edit=new ScheduleEditViewModel(parent,control.Plan);parent.Selected=control.Other;
            Check(edit.EnabledText=="已禁用"&&edit.EnableTaskCommand.CanExecute(null)&&!edit.DisableTaskCommand.CanExecute(null)&&edit.NextTimesCaption.Contains("启用后"),
                "禁用任务显示明确状态、启用入口及启用后计划时间");
            edit.Frequency=CronFrequency.Daily;edit.Hour=2;edit.Minute=10;var draft=edit.Cron;
            await edit.EnableTaskCommand.ExecuteAsync(null);
            Check(edit.Enabled&&edit.EnabledText=="已启用"&&!edit.EnableTaskCommand.CanExecute(null)&&edit.DisableTaskCommand.CanExecute(null),"服务确认启用后切换状态及可用操作");
            Check(control.Requests.Single() is{Command:"Resume",PluginId:"plugin",JobId:"target",ExpectedRevision:2}&&control.Other.Enabled&&edit.Cron==draft,
                "启用绑定打开的任务及修订号，不操作后来选中的其他任务，也不保存或丢失频率草稿");
            await edit.DisableTaskCommand.ExecuteAsync(null);
            Check(!edit.Enabled&&control.Requests.Last() is{Command:"Pause",ExpectedRevision:3}&&edit.Cron==draft,"禁用沿用最新修订，保留频率草稿");
            var saved=false;edit.Saved+=()=>saved=true;await edit.SaveCommand.ExecuteAsync(null);
            Check(saved&&control.Requests.Last() is{Command:"ChangeSchedule",ExpectedRevision:4}&&control.Plan.Cron==draft&&!control.Plan.Enabled,
                "启停后仍能保存计划，使用更新后的修订号且保留禁用状态");
            var denied=new ScheduleEditViewModel(parent,control.Plan);control.Forbidden=true;await denied.EnableTaskCommand.ExecuteAsync(null);
            Check(!denied.Enabled&&denied.EnabledStatusKnown&&denied.Status.Contains("管理权限"),"权限失败不把禁用任务显示为已启用");
            control.Forbidden=false;control.Conflict=true;await denied.EnableTaskCommand.ExecuteAsync(null);
            Check(!denied.Enabled&&denied.Status.Contains("更改"),"修订冲突显示失败，不猜测启用成功");control.Conflict=false;
            var uncertain=new ScheduleEditViewModel(parent,control.Plan);control.DropReply=true;
            await uncertain.EnableTaskCommand.ExecuteAsync(null);
            Check(!uncertain.EnabledStatusKnown&&uncertain.EnabledText.Contains("未确认")&&!uncertain.EnableTaskCommand.CanExecute(null)&&!uncertain.SaveCommand.CanExecute(null),
                "已受理但响应丢失时显示状态未确认，禁止创建第二次启停或覆盖计划");
            var id=control.Requests.Last().RequestId;await parent.ConfirmPendingCommand.ExecuteAsync(null);
            var reopened=new ScheduleEditViewModel(parent,parent.Jobs.Single(x=>x.JobId=="target"));
            Check(control.Requests.Last().RequestId==id&&reopened.Enabled&&control.Plan.Revision==6,"重新确认复用原请求，重新打开显示真实启用状态且不重复增加修订");

            var options = await parent.ReadPluginPlanOptionsAsync();
            Check(options is [{ Revision: 0 }, { Revision: 6 }], "创建清单先展示可添加任务并保留已有计划");
            var create = new ScheduleEditViewModel(parent, options![0]);
            var priorRequests = control.Requests.Count;
            await create.EnableTaskCommand.ExecuteAsync(null);
            Check(create.IsNewPlan && create.Enabled && control.Requests.Count == priorRequests, "创建时切换启用只修改草稿");
            create.Frequency = CronFrequency.Daily;
            create.Hour = 22;
            var created = false;
            create.Saved += () => created = true;
            await create.SaveCommand.ExecuteAsync(null);
            Check(created && control.Requests.Last() is { Command: WorkerProtocol.AddPluginPlan, JobId: "new", Payload: "Enabled" }
                && parent.Selected is { JobId: "new", Revision: 1, Enabled: true } && parent.Selected.Cron == create.Cron,
                "保存创建通过任务目录入口提交并选中后台确认的计划");

            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            var closed=new ScheduleEditViewModel(new(cancelled.Token),control.Plan);
            Check(!closed.EnableTaskCommand.CanExecute(null)&&!closed.DisableTaskCommand.CanExecute(null)&&!closed.SaveCommand.CanExecute(null),"会话关闭后禁止启用、禁用或保存");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class Control:IBackgroundControl
    {
        internal TaskDefinition Plan=new("plugin","target","合成禁用任务","0 * * * * ?","China Standard Time",false,2,false){PlanState="Disabled"};
        internal TaskDefinition Other=new("plugin","other","其他任务","0 * * * * ?","China Standard Time",true,1,false){PlanState="Waiting"};
        internal TaskDefinition Template = new("plugin", "new", "可添加任务", "0 0 21 * * ?", "China Standard Time", false, 0, false);
        internal readonly List<BackgroundRequest> Requests=[];
        private readonly HashSet<Guid> accepted=[];
        internal bool Forbidden,Conflict,DropReply;
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var now=CRS.Timing.BeijingClock.Now;
            BackgroundResponse Reply(string code)=>new(WorkerProtocol.Version,request.RequestId,code);
            if(request.Command=="GetHealth")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Health:new("Running",now,now,1)));
            if(request.Command=="GetTaskActivity")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",TaskActivity:new(now,[],0,0,0)));
            if(request.Command=="GetJobs")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Jobs:[Plan,Other]));
            if (request.Command == WorkerProtocol.GetPluginPlanOptions)
                return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok", Jobs: [Plan, Template]));
            if (request.Command == WorkerProtocol.AddPluginPlan)
            {
                Requests.Add(request);
                Template = Template with { Revision = 1, Cron = request.Cron!, Enabled = request.Payload == "Enabled" };
                return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok", Jobs: [Template]));
            }
            if(request.Command is not ("Pause" or "Resume" or "ChangeSchedule"))throw new Exception("非预期命令："+request.Command);
            Requests.Add(request);
            if(Forbidden)return Task.FromResult(Reply("Forbidden"));
            if(accepted.Contains(request.RequestId))return Task.FromResult(Reply("Ok"));
            if(Conflict||request.PluginId!=Plan.PluginId||request.JobId!=Plan.JobId||request.ExpectedRevision!=Plan.Revision)return Task.FromResult(Reply("InvalidOrStaleRequest"));
            Plan=request.Command switch
            {
                "Pause"=>Plan with{Enabled=false,Revision=Plan.Revision+1,PlanState="Disabled"},
                "Resume"=>Plan with{Enabled=true,Revision=Plan.Revision+1,PlanState="Waiting"},
                _=>Plan with{Cron=request.Cron!,TimeZoneId=request.TimeZoneId!,Revision=Plan.Revision+1}
            };
            accepted.Add(request.RequestId);
            if(DropReply){DropReply=false;throw new System.IO.IOException("合成受理后连接中断");}
            return Task.FromResult(Reply("Ok"));
        }
    }
}
