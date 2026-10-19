using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class EquityUiChecks
{
    public static void Run()
    {
        Exception? failure=null;var thread=new Thread(()=>
        {
            var app=new System.Windows.Application();var root=Path.Combine(Environment.CurrentDirectory,"artifacts","direct-equity-20261018","ui",Guid.NewGuid().ToString("N"));
            var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using(var seed=TestMarketData.Open(Path.Combine(root,"data"),raw))
            {
                DirectEquityChecks.Seed(seed);
                seed.SaveEquityReports(new("SSE","600036"),new(2026,8,1),new(2026,10,19),[EquityReportChecks.Page()]);
            }
            using var lifetime=new CancellationTokenSource();var workspace=new EtfWorkspace(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
            workspace.InitializeAsync(default).GetAwaiter().GetResult();var model=new EquityViewModel(workspace,lifetime.Token);var page=new EquityPage(model);
            var window=new System.Windows.Window{Content=page,Width=1200,Height=850,ShowInTaskbar=false,Title="直接持股隔离合成验收"};
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    await model.RefreshCommand.ExecuteAsync(null);if(model.Rows.Count!=3)throw new Exception("合成持股查询不完整。");
                    model.Selected=model.Rows.First(x=>x.Institution=="汇金资管");await model.DetailTask;await Task.Delay(600);window.UpdateLayout();
                    if(model.HoldingSeries.Length!=1||model.DeltaSeries.Length!=1||model.Disclosures.Count!=2||model.Events.Count!=2||model.Events[0].Delta!=-100||model.StockAccounts.Count!=3)throw new Exception("持仓点、增减柱、组合选择和披露明细未绑定。");
                    if(model.Matrix.Count!=1||!model.Matrix[0].Social.Text.Contains("报告期持股数增加")||!model.Matrix[0].Pension.Text.Contains("本期未列示"))throw new Exception("机构矩阵状态不完整。");
                    model.HoldingAxes[0].MinLimit=.2;if(model.DeltaAxes[0].MinLimit!=.2)throw new Exception("持仓缩放未同步增减图。");
                    model.ResetEquityZoomCommand.Execute(null);
                    model.HoldingPercent=true;var ratio=((LiveChartsCore.SkiaSharpView.ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>)model.HoldingSeries[0]).Values!.Last();
                    if(ratio.Y!=9)throw new Exception("比例不是原披露比例。");model.HoldingPercent=false;
                    var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth,(int)page.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(page);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    var output=Path.Combine(Environment.CurrentDirectory,"artifacts","direct-equity-20261018","equity-page-synthetic.png");using(var file=File.Create(output))encoder.Save(file);
                    model.MainTabIndex=1;await Task.Delay(200);window.UpdateLayout();
                    var matrixBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth,(int)page.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);matrixBitmap.Render(page);
                    var matrixEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder();matrixEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(matrixBitmap));
                    using(var file=File.Create(Path.Combine(Environment.CurrentDirectory,"artifacts","equity-charts-20261019","matrix-synthetic.png")))matrixEncoder.Save(file);
                    model.SelectedMatrix=model.Matrix.Single();model.ShowMatrixStockCommand.Execute(null);await model.DetailTask;
                    if(model.MainTabIndex!=0||model.Selected?.Ticker!="600036")throw new Exception("矩阵未打开对应股票持仓图。");
                    model.Selected=model.Rows.First(x=>x.Institution=="汇金资管");await model.DetailTask;
                    var eventTab=Descendants(page).OfType<System.Windows.Controls.TabItem>().Single(x=>Equals(x.Header,"历次变动"));
                    eventTab.IsSelected=true;await Task.Delay(200);window.UpdateLayout();
                    var eventBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth,(int)page.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);eventBitmap.Render(page);
                    var eventEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder();eventEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(eventBitmap));
                    using(var file=File.Create(Path.Combine(Environment.CurrentDirectory,"artifacts","direct-equity-20261018","equity-events-synthetic.png")))eventEncoder.Save(file);
                    model.SelectedAccount=model.StockAccounts.Single(x=>x.Row.Institution=="养老保险基金");await model.DetailTask;
                    if(model.Events.Count!=2||model.DeltaSeries.Length!=0||((LiveChartsCore.SkiaSharpView.ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>)model.HoldingSeries.Single()).Values!.Count()!=1)throw new Exception("未列示被填零或画成清仓柱。");
                    await workspace.SaveEquityDisclosureAsync("SSE","600036",OfficialEquitySource.Request("SSE","600036"),DirectEquityChecks.Source(new(2026,10,16),("中央汇金资产管理有限责任公司",800,8)),default);
                    await model.RefreshAsync();model.Selected=model.Rows.Single(x=>x.State=="NeedsReview");await model.DetailTask;
                    var candidate=model.SelectedDisclosure;
                    if(model.Reports.Count!=1)throw new Exception("缓存官方目录未加载。");
                    model.OriginalConfirmed=true;model.Comparable=true;model.SelectedReport=model.Reports.Single();model.UseReportCommand.Execute(null);
                    if(model.DocumentUrl!=model.SelectedReport.DocumentUrl||model.Published!=new DateTime(2026,8,29)||model.ReportAsOf is not null||model.OriginalConfirmed||model.Comparable)throw new Exception("报告线索没有安全填入表单或被自动批准为持仓。");
                    Descendants(page).OfType<System.Windows.Controls.TabItem>().Single(x=>Equals(x.Header,"披露证据 / 核验")).IsSelected=true;
                    await Task.Delay(100);window.UpdateLayout();Descendants(page).OfType<System.Windows.Controls.GroupBox>().Single().BringIntoView();await Task.Delay(200);window.UpdateLayout();
                    var reportsBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth,(int)page.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);reportsBitmap.Render(page);
                    var reportsEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder();reportsEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(reportsBitmap));
                    using(var file=File.Create(Path.Combine(Environment.CurrentDirectory,"artifacts","equity-disclosures-20261019","reports-synthetic.png")))reportsEncoder.Save(file);
                    model.HoldingPercent=true;
                    if(candidate?.Id!=model.Selected.DisclosureId||model.SelectedDisclosure!=candidate||model.SelectedEvent is not null)throw new Exception("图表默认选择或单位切换覆盖了待审候选。");
                    var detachedAxis=model.HoldingAxes.First();
                    model.Institution="养老保险基金";await model.LoadTask;if(model.Rows.Count!=1||model.Rows[0].State!="NotListed")throw new Exception("机构筛选未保持未知持仓。");
                    model.Search="无此组合";await model.LoadTask;if(model.Rows.Count!=0||model.HoldingSeries.Length!=0||model.DeltaSeries.Length!=0||model.Disclosures.Count!=0||model.Events.Count!=0||model.Matrix.Count!=0||model.StockAccounts.Count!=0||model.Reports.Count!=0)throw new Exception("空结果残留旧详情。");
                    if(detachedAxis is not null)detachedAxis.MinLimit=.25;
                    CheckUnsafeChartInputs(workspace);
                    Console.WriteLine("PASS 真实WPF直接持股：上下图、比例、联动缩放、组合切换、矩阵、空结果及不安全输入隔离；截图为合成数据。");
                }
                catch(Exception error){failure=error;}
                finally{lifetime.Cancel();await workspace.DisposeAsync();window.Close();}
            };
            app.Run(window);
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw new InvalidOperationException("直接持股UI验收失败。",failure);
    }
    private static void CheckUnsafeChartInputs(IEtfWorkspace workspace)
    {
        var model=new EquityViewModel(workspace,CancellationToken.None);
        foreach(var (state,shares,delta) in new[]{("NeedsReview",100m,999m),("Critical",100m,999m),("Conflict",100m,999m),("NotComparable",100m,999m),("Increased",200m,100m),("Decreased",100m,-100m),("Unchanged",100m,0m)})
            model.Events.Add(new(Guid.NewGuid().ToString(),"SSE","600036","合成股票","社保基金","合成组合",shares,1,new(2026,3,31),new(2026,4,28),new(2026,4,28),state,delta,"Portfolio",false,"https://www.sse.com.cn/test.pdf"));
        model.HoldingPercent=true;
        var changes=((LiveChartsCore.SkiaSharpView.ColumnSeries<LiveChartsCore.Defaults.ObservablePoint>)model.DeltaSeries.Single()).Values!.ToArray();
        if(changes.Length!=3||changes[0].Y!=100||changes[1].Y!=-100||changes[2].Y!=0)throw new Exception("未核验、异常、冲突或不可比数据进入柱图。");
        if(((LiveChartsCore.SkiaSharpView.ScatterSeries<LiveChartsCore.Defaults.ObservablePoint>)model.HoldingSeries.Single()).Values!.Count()!=4)throw new Exception("不可信持仓进入点图，或不可比持仓点丢失。");
    }
    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent)
    {
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);yield return child;
            foreach(var nested in Descendants(child))yield return nested;
        }
    }
}
