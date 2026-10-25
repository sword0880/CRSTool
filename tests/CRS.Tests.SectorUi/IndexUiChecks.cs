using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class IndexUiChecks
{
    public static void Run(string file,string screenshot)
    {
        var report=JsonSerializer.Deserialize<IndexSeriesResearchReport>(File.ReadAllText(file))??throw new InvalidDataException("隔离指数报告缺失。");
        // 此处显式启用合成准入状态以验证按钮；不代表正式来源用途许可。
        report=report with{PriceCollectionAllowed=true,ValuationCollectionAllowed=true};Exception? failure=null;var passed=0;
        var thread=new Thread(()=>
        {
            Window? window=null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                void Check(bool value,string name){if(!value)throw new InvalidDataException(name);passed++;Console.WriteLine("PASS 指数界面："+name);}
                void Pump(Task? task=null)
                {
                    var watch=Stopwatch.StartNew();do{var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(80)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);if(watch.Elapsed>TimeSpan.FromSeconds(12))throw new TimeoutException("指数界面检查超时。");}while(task is not null?!task.IsCompleted:watch.Elapsed<TimeSpan.FromMilliseconds(1600));task?.GetAwaiter().GetResult();
                }
                var workspace=DispatchProxy.Create<IEtfWorkspace,IndexTestWorkspace>();var proxy=(IndexTestWorkspace)(object)workspace;proxy.Report=report;
                using var life=new CancellationTokenSource();var model=new IndexResearchViewModel(workspace,life.Token){From=report.From.ToDateTime(TimeOnly.MinValue),Through=report.Through.ToDateTime(TimeOnly.MinValue)};
                window=new(){Title="公开原件隔离指数页面验证",Content=new IndexResearchPage(model,false),Width=1280,Height=1000,MaxHeight=SystemParameters.WorkArea.Height,Background=SystemColors.WindowBrush};window.Show();Pump(model.QueryCommand.ExecuteAsync(null));
                Check(model.Rows.Count==11&&model.Series.Length==11,"10行业样本指数和宽基参照来自后端报告");
                Check(model.Rows.All(x=>x.Row.AcceptedPriceDays==4&&x.Row.Curve[0].NormalizedLevel==100&&x.Row.Curve[^1].NormalizedLevel.HasValue)&&model.Rows.Any(x=>x.Row.Curve[^1].NormalizedLevel!=100),"每条真实曲线都有共同起点和有效末值，不能用空系列通过");
                Check(model.Rows.Single(x=>x.Row.Code=="399381").Row.PublishedPb==1.42m,"能源PB与真实原件一致");
                Check(model.SnapshotDate==new DateTime(2026,10,23),"估值默认日期由已登记价格推定而非猜星期");
                Check(model.Rows.All(x=>x.Row.PbPriorSamples==0&&x.Row.CoverageState=="Unknown"&&x.Row.OpportunityState.StartsWith("NotEvaluated",StringComparison.Ordinal)),"当期PB不补造历史、覆盖率或机会");
                model.Rows[0].IsVisible=false;Check(model.Series.Length==10,"选择仅改变可见曲线");model.Rows[0].IsVisible=true;
                model.Status="真实公开原件隔离复算；工作区/取得时点/日历为显式测试夹具，非正式服务。"+model.Status;Pump();Pump();
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);using(var output=File.Create(screenshot)){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(output);}
                Pump(model.QueuePricesCommand.ExecuteAsync(null));Check(proxy.Queued==1&&model.TaskStatus.Contains("已受理",StringComparison.Ordinal)&&!model.TaskStatus.Contains("已完成",StringComparison.Ordinal),"受理不能显示采集完成");
                proxy.Code="NeedsReview";Pump(model.RefreshTaskCommand.ExecuteAsync(null));Check(model.TaskStatus.Contains("需要核对",StringComparison.Ordinal),"待审不会升级为全部成功");
                proxy.Code="Timeout";Pump(model.RefreshTaskCommand.ExecuteAsync(null));Check(model.TaskStatus.Contains("已结束",StringComparison.Ordinal)&&model.TaskStatus.Contains("Timeout",StringComparison.Ordinal),"失败终态不能显示还在受理");
                proxy.Mode="Failure";Pump(model.QueryCommand.ExecuteAsync(null));Check(model.Rows.Count==0&&model.Series.Length==0&&model.Status.Contains("失败",StringComparison.Ordinal),"查询失败清除旧图");
                proxy.Mode="Wait";var old=model.QueryCommand.ExecuteAsync(null);Pump();model.From=model.From!.Value.AddDays(1);proxy.Pending.TrySetResult(report);Pump(old);Check(model.Rows.Count==0&&model.Status.Contains("日期已改变",StringComparison.Ordinal),"晚到的旧日期报告不能覆盖新选择");
                proxy.Manage=false;Check(!model.QueuePricesCommand.CanExecute(null)&&!model.QueueValuationCommand.CanExecute(null),"只读角色没有采集操作");
                proxy.Manage=true;proxy.Mode="Normal";proxy.Report=report with{PriceCollectionAllowed=false,ValuationCollectionAllowed=false};Pump(model.QueryCommand.ExecuteAsync(null));
                Check(model.Rows.Count==11&&!model.QueuePricesCommand.CanExecute(null)&&!model.QueueValuationCommand.CanExecute(null)&&model.SourceStatus.Contains("未获准",StringComparison.Ordinal),"来源许可未核验仍能查询，但采集按钮关闭并说明原因");
                File.WriteAllText(Path.ChangeExtension(screenshot,".json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Transport="CapturedOfficialReplayWithUiWorkspaceFixture",report.Fingerprint},new JsonSerializerOptions{WriteIndented=true}));
            }
            catch(Exception error){failure=error;}
            finally{window?.Close();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
        Console.WriteLine($"PASS 指数界面合计 {passed}；非正式服务或机会验证。");
    }
}
public class IndexTestWorkspace:DispatchProxy
{
    public IndexSeriesResearchReport Report {get;set;}=null!;public string Mode="Normal",Code="Succeeded";public bool Manage=true;public int Queued;public TaskCompletionSource<IndexSeriesResearchReport> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);private readonly Guid request=Guid.NewGuid();
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {
        "get_CanManage"=>Manage,
        "IndexSeriesReportAsync"=>Mode=="Failure"?Task.FromException<IndexSeriesResearchReport>(new IOException("隔离读取失败")):Mode=="Wait"?Pending.Task:Task.FromResult(Report),
        "QueueIndexSeriesAsync" or "QueueIndexValuationAsync"=>Queue(),
        "IndexSeriesStatusAsync"=>Task.FromResult(new IndexSeriesCommandReceipt(request,"Completed",Code,"fixture")),
        _=>throw new NotSupportedException("指数界面夹具不支持此操作。")
    };
    private Task<IndexSeriesCommandReceipt> Queue(){Queued++;return Task.FromResult(new IndexSeriesCommandReceipt(request,"Pending","Accepted",null));}
}
