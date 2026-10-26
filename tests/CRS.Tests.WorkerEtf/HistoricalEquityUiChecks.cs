using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Presentation;
using CRS.Worker.Ipc;

internal static class HistoricalEquityUiChecks
{
    public static void Run(string pipe,string output,bool readOnly=false,Guid? originalRequest=null)
    {
        Exception? failure=null;var thread=new Thread(()=>
        {
            var app=new Application();app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(readOnly?90:45));var model=new EquityViewModel(readOnly?new WorkerEtfWorkspace():new WorkerEtfWorkspace(new BackgroundPipeClient(pipe)),stop.Token);var page=new EquityPage(model,false);
            var window=new Window{Content=page,Width=1180,Height=850,ShowInTaskbar=false,ShowActivated=false,Title=readOnly?"历史披露正式只读核验":"历史原文登记隔离验证"};
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    await model.RefreshCommand.ExecuteAsync(null);await model.LoadHistoricalDocumentsCommand.ExecuteAsync(null);
                    if(model.HistoricalDocuments.Count!=1||model.ImportHistoricalPdfCommand.CanExecute(null))throw new Exception("目录或未确认原文的按钮状态错误。");
                    var tab=FindTabs(page).SelectMany(x=>x.Items.OfType<TabItem>()).Single(x=>x.Header?.ToString()=="补入历史披露");tab.IsSelected=true;window.UpdateLayout();
                    if(readOnly)
                    {
                        // 显式恢复已经保存的真实请求，只查询，不设置审核人/原文确认，也不调用提交命令。
                        model.Selected=model.Rows.Single(x=>x.Ticker=="600153"&&x.Account=="基本养老保险基金一零零三组合");await model.DetailTask;
                        model.HistoricalRequestId=originalRequest??throw new InvalidDataException("只读核验必须给出原请求ID。");
                        await model.CheckHistoricalImportCommand.ExecuteAsync(null);
                        // 刷新列表会经双向选择绑定重建详情；只读验收须等本轮详情完成，而非把瞬时空集合判作事实丢失。
                        await model.DetailTask;
                        if(model.HistoricalState!="Completed"||!model.HistoricalStatus.Contains("核验 4 个具名账户")||model.ImportHistoricalPdfCommand.CanExecute(null)
                            ||model.Disclosures.Count(x=>x.State=="Accepted")!=2||model.Events.Count!=2||model.Events.Any(x=>x.Delta is not null))
                            throw new Exception($"正式历史边界不符：state={model.HistoricalState};status={model.HistoricalStatus};canImport={model.ImportHistoricalPdfCommand.CanExecute(null)};disclosures={model.Disclosures.Count};events={model.Events.Count};detail={model.DetailStatus}。");
                    }
                    else
                    {
                    model.HistoricalReviewer="isolated-ui";model.HistoricalReason="isolated repeated document";model.HistoricalOriginalConfirmed=true;
                    if(!model.ImportHistoricalPdfCommand.CanExecute(null))throw new Exception("显式确认后未允许登记。");
                    await model.ImportHistoricalPdfCommand.ExecuteAsync(null);
                    if(model.HistoricalRequestId is null)throw new Exception("受理后未保留请求编号。");
                    while(model.HistoricalState!="Completed"){stop.Token.ThrowIfCancellationRequested();await Task.Delay(150,stop.Token);await model.CheckHistoricalImportCommand.ExecuteAsync(null);}
                    if(!model.HistoricalStatus.Contains("没有新增持仓事实")||model.Rows.Count!=4)throw new Exception("既有原文复用被界面显示为新事实或查询未刷新。");
                    }
                    await Task.Delay(500,stop.Token);window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                    Directory.CreateDirectory(output);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"historical-pdf.png"));encoder.Save(file);
                    Console.WriteLine(readOnly?"PASS 正式历史WPF只读：原请求同ID回查、两期披露和不可比差额；未调用提交。":"PASS 历史原文WPF：目录不等于登记、显式确认、稳定请求、主动回查和既有事实复用。");
                }
                catch(Exception error){failure=error;}
                finally{stop.Cancel();window.Close();}
            };
            app.Run(window);
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw new InvalidOperationException("历史原文UI隔离验收失败。",failure);
    }
    private static IEnumerable<TabControl> FindTabs(DependencyObject root)
    {if(root is TabControl tabs)yield return tabs;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var nested in FindTabs(VisualTreeHelper.GetChild(root,i)))yield return nested;}
}
