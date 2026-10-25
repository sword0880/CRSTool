using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

if(args is ["--industry",var industryReport,var industryImage]){IndustryUiChecks.Run(industryReport,industryImage);return;}
if(args is ["--industry-installed",var installedFrom,var installedThrough,var installedImage]){IndustryInstalledUiChecks.Run(DateOnly.Parse(installedFrom),DateOnly.Parse(installedThrough),installedImage);return;}
if(args is ["--index",var indexReport,var indexImage]){IndexUiChecks.Run(indexReport,indexImage);return;}
if(args is ["--index-installed",var indexFrom,var indexThrough,var indexInstalledImage]){IndexInstalledUiChecks.Run(DateOnly.Parse(indexFrom),DateOnly.Parse(indexThrough),indexInstalledImage);return;}

if(args.Length!=2)throw new ArgumentException("需要合成输入文件与截图路径。");
// 财务计算在领域层，视图仅接收报告；夹具标题明确区别真实行业行情。
var input=JsonSerializer.Deserialize<SectorStudyRequest>(File.ReadAllText(args[0]))!;
input=input with{Sectors=input.Sectors.Select((s,i)=>i==0?s:s with{Prices=s.Prices.Select((p,n)=>p with{Close=p.Close*(1m+n/3000m)}).ToArray(),
    Valuations=s.Valuations.Select(v=>v.Date==input.Through?v with{PeTtm=30,Pb=3}:v).ToArray()}).ToArray()};
var report=SectorResearch.Calculate(input);Exception? failure=null;
var thread=new Thread(()=>
{
    try
    {
        var model=new SectorResearchViewModel(report,"合成资料验证 · 非真实行业行情或投资机会");
        var window=new Window{Title="行业研究隔离界面检查",Content=new SectorResearchPage(model),Width=1150,Height=800,MaxHeight=SystemParameters.WorkArea.Height,Background=SystemColors.WindowBrush};
        window.Show();
        void Pump(){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
        Pump();if(model.Series.Length!=2)throw new Exception("不同行业未生成独立曲线。");
        model.Metric=model.Metrics[1];Pump();if(model.YAxes[0].MaxLimit!=100)throw new Exception("分位曲线单位错误。");
        model.Rows[1].IsVisible=false;if(model.Series.Length!=1)throw new Exception("行业选择没有更新图表。");
        model.Rows[1].IsVisible=true;model.Metric=model.Metrics[0];Pump();
        var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);using var file=File.Create(args[1]);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(file);window.Close();
        Console.WriteLine("PASS 行业真实窗口：两行业独立曲线、走势/分位切换、筛选及合成来源说明；不是主导航或真实数据验收。");
    }
    catch(Exception error){failure=error;}
});
thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
