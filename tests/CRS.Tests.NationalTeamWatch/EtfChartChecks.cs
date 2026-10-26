using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class EtfChartChecks
{
    public static async Task Run()
    {
        var workspace=new ChartWorkspace();using var lifetime=new CancellationTokenSource();
        var model=new EtfViewModel(workspace,lifetime.Token){Busy=true};
        EtfSummary Row(string ticker)=>new("SSE",ticker,ticker,"test","DataPilot",null,null,null,null,null,"",0);
        model.Selected=Row("510300");var first=model.HistoryLoadTask;
        model.Selected=Row("510050");var second=model.HistoryLoadTask;
        if(workspace.Requests.Count!=2)throw new Exception("忙碌时点击ETF未自动加载。");
        workspace.Requests[1].Reply.SetResult([new(new(2026,10,13),200,10,1,1)]);await second;
        workspace.Requests[0].Reply.SetResult([new(new(2026,10,12),100,-10,1,1)]);await first;
        if(!model.ChartStatus.StartsWith("510050") || model.Series.Length!=1)throw new Exception("旧图表覆盖新选择。");
        model.Busy=false;await model.RefreshMarketActivityCommand.ExecuteAsync(null);
        if(!model.XAxes[0].Labels!.SequenceEqual(model.MarketAmountAxes[0].Labels!) || model.XAxes[0].Labels!.Count!=3)throw new Exception("上下图日期未对齐。");
        var shares=((LiveChartsCore.SkiaSharpView.LineSeries<LiveChartsCore.Defaults.ObservablePoint>)model.Series[0]).Values!.ToArray();
        if(shares.Length!=1 || shares[0].X!=2 || shares[0].Y!=200)throw new Exception("缺失ETF日期被错位或填零。");
        model.XAxes[0].MinLimit=0.25;model.XAxes[0].MaxLimit=1.75;
        if(model.MarketAmountAxes[0].MinLimit!=0.25 || model.MarketAmountAxes[0].MaxLimit!=1.75)throw new Exception("上图缩放没有同步下图。");
        model.MarketAmountAxes[0].MinLimit=0.5;if(model.XAxes[0].MinLimit!=0.5)throw new Exception("下图缩放没有同步上图。");
        model.HoverComparisonAt(2);
        if(!model.ComparisonReadout.Contains("2026-10-13") || !model.ComparisonReadout.Contains("200 份") || !model.ComparisonReadout.Contains("市场成交额 无有效数据") || model.ShareSections.Length!=1 || model.AmountSections.Length!=1)throw new Exception("联动悬停没有显示同日缺口。");
        model.ResetComparisonZoomCommand.Execute(null);if(model.ShareSections.Length!=0 || model.XAxes[0].MinLimit!=-0.5 || model.MarketAmountAxes[0].MaxLimit!=2.5)throw new Exception("重置缩放/参考线失败。");
        model.AllEtfs=true;var all=model.HistoryLoadTask;
        if(workspace.Requests[^1].Key is not null)throw new Exception("汇总仍限定单只ETF。");
        workspace.Requests[^1].Reply.SetResult([new(new(2026,10,13),null,null,1,2)]);await all;
        if(!model.ChartStatus.Contains("有效 1/2") || !model.ChartStatus.Contains("不可计算"))throw new Exception("汇总缺口提示错误。");
        if(((LiveChartsCore.SkiaSharpView.LineSeries<LiveChartsCore.Defaults.ObservablePoint>)model.Series[0]).Values!.Any())throw new Exception("全缺失曲线生成了数值。");
        model.Selected=Row("510050");var gap=model.HistoryLoadTask;
        workspace.Requests[^1].Reply.SetResult([new(new(2026,10,11),100,null,2,2),new(new(2026,10,12),null,null,1,2),new(new(2026,10,13),200,null,2,2)]);await gap;
        shares=((LiveChartsCore.SkiaSharpView.LineSeries<LiveChartsCore.Defaults.ObservablePoint>)model.Series[0]).Values!.ToArray();
        if(shares.Length!=2 || shares[0].X!=0 || shares[1].X!=2 || shares[1].Y!=200)throw new Exception("跨缺日连线丢失原日期索引。");
        model.HoverComparisonAt(1);
        if(!model.ComparisonReadout.Contains("总份额 无有效数据") || !model.ComparisonReadout.Contains("净增减 不可计算") || !model.ComparisonReadout.Contains("2.00 亿元"))throw new Exception("跨缺日连线伪造同日读数或导致成交额错位。");
        model.Selected=Row("510300");var single=model.HistoryLoadTask;
        workspace.Requests[^1].Reply.SetResult([]);await single;
        if(model.AllEtfs || !model.ChartStatus.Contains("暂无历史"))throw new Exception("点击ETF没有恢复单只模式。");
        model.AllEtfs=true;var pending=model.HistoryLoadTask;lifetime.Cancel();
        workspace.Requests[^1].Reply.SetResult([new(new(2026,10,13),300,20,2,2)]);await pending;
        if(model.Series.Length!=0)throw new Exception("会话取消后图表仍回写。");
        Console.WriteLine("PASS ETF自动图表：忙碌点击、快速切换、汇总、缺口、回到单只及会话取消。");
    }
    public static void RunGapUi()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var app=new System.Windows.Application();
            var workspace=new ChartWorkspace();using var lifetime=new CancellationTokenSource();
            var model=new EtfViewModel(workspace,lifetime.Token);
            var chart=new LiveChartsCore.SkiaSharpView.WPF.CartesianChart{Width=900,Height=400,AnimationsSpeed=TimeSpan.Zero,Background=System.Windows.Media.Brushes.White};
            var window=new System.Windows.Window{Content=chart,SizeToContent=System.Windows.SizeToContent.WidthAndHeight,ShowInTaskbar=false};
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    var load=model.ShowHistoryCommand.ExecuteAsync(null);
                    workspace.Requests[^1].Reply.SetResult([new(new(2026,10,11),100,null,2,2),new(new(2026,10,12),null,null,1,2),new(new(2026,10,13),200,null,2,2)]);
                    await load;await model.RefreshMarketActivityCommand.ExecuteAsync(null);
                    chart.Series=model.Series;chart.XAxes=model.XAxes;
                    chart.YAxes=[new LiveChartsCore.SkiaSharpView.Axis{MinLimit=50,MaxLimit=250}];
                    await Task.Delay(1200);window.UpdateLayout();
                    var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(900,400,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(chart);
                    var center=chart.ScaleDataToPixels(new LiveChartsCore.Drawing.LvcPointD(1,150));
                    var pixels=new byte[900*400*4];bitmap.CopyPixels(pixels,900*4,0);
                    var blue=false;
                    for(var y=(int)center.Y-4;y<=(int)center.Y+4;y++)for(var x=(int)center.X-4;x<=(int)center.X+4;x++)
                    {
                        if(x<0 || x>=900 || y<0 || y>=400)continue;
                        var offset=(y*900+x)*4;
                        if(pixels[offset]>200 && pixels[offset+1]>60 && pixels[offset+1]<200 && pixels[offset+2]<90)blue=true;
                    }
                    var directory=Path.Combine(Environment.CurrentDirectory,"artifacts","etf-gap-20261017");Directory.CreateDirectory(directory);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using(var file=File.Create(Path.Combine(directory,"gap-connected.png")))encoder.Save(file);
                    if(!blue)throw new Exception("真实WPF渲染未跨过中间缺日连接有效观测点。");
                    Console.WriteLine("PASS 真实WPF像素验证：缺日中点有连线；截图为合成数据。");
                }
                catch(Exception error){failure=error;}
                finally{window.Close();}
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("缺日连线渲染检查失败。",failure);
    }
    private sealed class ChartWorkspace:INationalTeamWorkspace
    {
        public List<(EtfKey? Key,TaskCompletionSource<List<EtfHistoryPoint>> Reply)> Requests {get;}=[];
        public bool CanManage => true;
        public string NetworkStatus=>"test";
        public Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token)=>Task.FromResult(new List<MarketActivityPoint>{new(new(2026,10,11),100000000m,1,"Complete"),new(new(2026,10,12),200000000m,1,"Complete"),new(new(2026,10,13),null,null,"Incomplete")});
        public Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token)
        {
            var reply=new TaskCompletionSource<List<EtfHistoryPoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((key,reply));return reply.Task;
        }
        public Task<List<EtfSummary>> OverviewAsync(CancellationToken token)=>throw new NotSupportedException();
        public Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>throw new NotSupportedException();
        public Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token)=>throw new NotSupportedException();
        public Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token)=>throw new NotSupportedException();
        public Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token)=>throw new NotSupportedException();
        public Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>throw new NotSupportedException();
        public Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token)=>throw new NotSupportedException();
        public Task ExportEvidenceAsync(string id,bool original,string destination,CancellationToken token)=>throw new NotSupportedException();
        public Task ReviewAsync(string id,bool accept,string reviewer,bool comparable,CancellationToken token)=>throw new NotSupportedException();
        public Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token)=>throw new NotSupportedException();
    }
}
