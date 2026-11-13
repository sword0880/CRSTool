using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.Plugin.Wpf;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class IndustryUiChecks
{
    public static void Run(string reportFile,string screenshot)
    {
        var report=JsonSerializer.Deserialize<IndustryPeReport>(File.ReadAllText(reportFile))??throw new InvalidDataException("没有隔离回放报告。");
        var isSynthetic=Path.GetFileName(reportFile).StartsWith("synthetic-",StringComparison.OrdinalIgnoreCase);
        Exception? failure=null;var passed=0;
        var thread=new Thread(()=>
        {
            Window? window=null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                void Check(bool ok,string label){if(!ok)throw new InvalidDataException(label);passed++;Console.WriteLine("PASS 行业界面："+label);}
                void Pump(Task? work=null)
                {
                    var clock=Stopwatch.StartNew();
                    do
                    {
                        var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
                        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
                        if(clock.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("行业界面检查等待超过10秒。");
                    }while(work is not null?!work.IsCompleted:clock.Elapsed<TimeSpan.FromMilliseconds(1500));
                    work?.GetAwaiter().GetResult();
                }
                var workspace=DispatchProxy.Create<IIndustryValuationWorkspace,IndustryTestWorkspace>();var proxy=(IndustryTestWorkspace)(object)workspace;proxy.Report=report;
                using var lifetime=new CancellationTokenSource();var model=new IndustryResearchViewModel(workspace,lifetime.Token){From=report.From.ToDateTime(TimeOnly.MinValue),Through=report.Through.ToDateTime(TimeOnly.MinValue)};
                window=new(){Title=isSynthetic?"行业合成数据界面验证":"行业输入报告隔离界面验证",Content=new IndustryResearchPage(model,false),Width=1280,Height=1000,MaxHeight=SystemParameters.WorkArea.Height,Background=SystemColors.WindowBrush};window.Show();
                Pump(model.QueryCommand.ExecuteAsync(null));
                Check(model.Rows.Count==11&&model.Series.Length==11,"11行业来自后端报告，未用默认合成曲线填补");
                Check(model.Rows.Single(x=>x.Row.Code=="C07").Row.LatestRollingWeighted==
                    report.Rows.Single(x=>x.Code=="C07").LatestRollingWeighted,"金融加权滚动PE来自报告，界面不修改金融数值");
                Check(model.Rows.All(x=>x.Row.PublishedSeriesPercentile is null),"单日原件不会产生虚假历史分位");
                model.Rows[0].IsVisible=false;Check(model.Series.Length==10,"行业选择只改变展示");model.Rows[0].IsVisible=true;
                model.Metric=model.Metrics[1];Check(model.YAxes[0].Name==model.Metrics[1],"加权与中位数切换保持单位标签");model.Metric=model.Metrics[0];
                model.Status=(isSynthetic?"明确合成的金融样本；":"输入报告的隔离回放；")+"非正式服务或完整历史验收。"+model.Status;
                Pump();Pump();var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);using(var file=File.Create(screenshot)){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(file);}
                // 显式改造的UI合成反例：真实原件回放截图不含这些人工分位。
                var candidate=report.Rows[0] with{State="PublishedSeriesResearch",PublishedSeriesPercentile=5m,
                    PublishedMedianSeriesPercentile=10m,LatestRollingWeighted=8m,LatestRollingMedian=9m,
                    PriorSamples=260,MedianPriorSamples=260,ExpectedPriorDays=260};
                proxy.Report=report with{Rows=report.Rows.Select((x,i)=>i==0?candidate:x).ToArray()};
                Pump(model.QueryCommand.ExecuteAsync(null));
                Check(model.ComparisonRows.Count==11&&model.ComparisonRows[0].Row.Code==candidate.Code
                    &&model.ComparisonRows[0].HistoricalPosition.Contains("双口径历史低位",StringComparison.Ordinal),"行业低位比较结果排首，但没有生成投资判断");
                model.OnlyDualLow=true;
                Check(model.ComparisonRows.Count==1&&model.Series.Length==1,"只看双口径低位同时筛选表格与曲线");
                model.OnlyDualLow=false;proxy.Report=report;Pump(model.QueryCommand.ExecuteAsync(null));
                Check(model.ComparisonRows.Count==11&&model.ComparisonRows.All(x=>x.Observation==IndustryPeObservation.EvidenceIncomplete),"真实单日样本不能由UI补造中位分位");
                Pump(model.QueueCommand.ExecuteAsync(null));Check(proxy.Queued==1&&model.TaskStatus.Contains("尚未确认",StringComparison.Ordinal),"受理不能显示采集已完成");
                proxy.Mode="Failure";Pump(model.QueryCommand.ExecuteAsync(null));Check(model.Rows.Count==0&&model.Series.Length==0&&model.Status.Contains("失败",StringComparison.Ordinal),"查询失败清除旧图，不能保留成当前结果");
                proxy.Mode="Normal";Pump(model.QueryCommand.ExecuteAsync(null));
                Check(model.Rows.Count>0,"权限故障前已有真实报告投影，验证不会残留旧结果");
                proxy.Mode="AccessDenied";Pump(model.QueryCommand.ExecuteAsync(null));
                Check(!model.Busy&&model.Rows.Count==0&&model.Series.Length==0&&model.Status.Contains("失败",StringComparison.Ordinal),"权限拒绝清除旧资料并显示错误，不逃逸到界面线程");
                var deniedStart=new IndustryResearchViewModel(workspace,CancellationToken.None);
                Pump(deniedStart.EnsureInitializedAsync());
                Check(!deniedStart.Busy&&deniedStart.Rows.Count==0&&deniedStart.Status.Contains("失败",StringComparison.Ordinal),"首次Loaded权限拒绝也正常结束初始化");
                proxy.Mode="Wait";var pending=model.QueryCommand.ExecuteAsync(null);Pump();model.CancelCommand.Execute(null);Pump(pending);
                Check(model.Rows.Count==0&&model.Status.Contains("取消",StringComparison.Ordinal)&&proxy.Queued==1,"取消只终止查询等待，不撤销已受理采集");
                Pump(model.RefreshTaskCommand.ExecuteAsync(null));Check(model.TaskStatus.Contains("差异待审核",StringComparison.Ordinal),"采集完成但待审不写成全部成功");
                proxy.ReceiptCode="Timeout";Pump(model.RefreshTaskCommand.ExecuteAsync(null));Check(model.TaskStatus.Contains("已结束",StringComparison.Ordinal)&&model.TaskStatus.Contains("Timeout",StringComparison.Ordinal)&&!model.TaskStatus.Contains("已受理",StringComparison.Ordinal),"调度失败终态不能显示仍在等待受理");
                using var sessionLifetime=new CancellationTokenSource();var session=new NationalTeamPluginEntry().CreateSession(null!);Pump(session.StartAsync(sessionLifetime.Token).AsTask());
                Check(((IPluginWpfSession)session).GetPages() is {Count:1} pages&&pages[0].RouteKey=="national-team-workspace","插件只注册分类工作台且启动不主动查询");Pump(session.DisposeAsync().AsTask());
                File.WriteAllText(Path.ChangeExtension(screenshot,".json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,InputReport=reportFile,report.Fingerprint,Transport=isSynthetic?"SyntheticUiFixture":"IsolatedInputReportWithUiWorkspaceFixture"},new JsonSerializerOptions{WriteIndented=true}));
            }
            catch(Exception error){failure=error;}
            finally{window?.Close();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
        Console.WriteLine($"PASS 行业真实窗口合计 {passed}；界面工作区为夹具，非正式Worker验收。");
    }
}

/// <summary>只替代界面工作区传输，明确保留传入报告的来源边界；队列状态为合成反例。</summary>
public class IndustryTestWorkspace:DispatchProxy
{
    public IndustryPeReport Report {get;set;}=null!;public string Mode="Normal";public string ReceiptCode="NeedsReview";public int Queued;private readonly Guid request=Guid.NewGuid();
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {
        "get_CanManage"=>true,
        "get_NetworkStatus"=>"界面隔离验证",
        "IndustryPeReportAsync"=>Mode=="AccessDenied"?Task.FromException<IndustryPeReport>(new UnauthorizedAccessException("隔离权限拒绝")):Mode=="Failure"?Task.FromException<IndustryPeReport>(new IOException("隔离查询失败")):Mode=="Wait"?WaitAsync((CancellationToken)args![2]!):Task.FromResult(Report),
        "QueueIndustryPeAsync"=>Queue(),
        "IndustryPeSyncStatusAsync"=>Task.FromResult(new IndustryPeSyncReceipt(request,"Completed",ReceiptCode,"fixture-execution")),
        _=>throw new NotSupportedException("界面夹具不支持此操作。")
    };
    private Task<IndustryPeSyncReceipt> Queue(){Queued++;return Task.FromResult(new IndustryPeSyncReceipt(request,"Pending","Accepted",null));}
    private static async Task<IndustryPeReport> WaitAsync(CancellationToken token){await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException("不会返回的查询");}
}
