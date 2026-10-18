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
            var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using(var seed=TestMarketData.Open(Path.Combine(root,"data"),raw))DirectEquityChecks.Seed(seed);
            using var lifetime=new CancellationTokenSource();var workspace=new EtfWorkspace(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
            workspace.InitializeAsync(default).GetAwaiter().GetResult();var model=new EquityViewModel(workspace,lifetime.Token);var page=new EquityPage(model);
            var window=new System.Windows.Window{Content=page,Width=1200,Height=850,ShowInTaskbar=false,Title="直接持股隔离合成验收"};
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    await model.RefreshCommand.ExecuteAsync(null);if(model.Rows.Count!=3)throw new Exception("合成持股查询不完整。");
                    model.Selected=model.Rows.First(x=>x.Institution=="汇金资管");await model.DetailTask;await Task.Delay(600);window.UpdateLayout();
                    if(model.HoldingSeries.Length!=1||model.Disclosures.Count!=2)throw new Exception("持仓点和披露明细未绑定。");
                    var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)page.ActualWidth,(int)page.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(page);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    var output=Path.Combine(Environment.CurrentDirectory,"artifacts","direct-equity-20261018","equity-page-synthetic.png");using(var file=File.Create(output))encoder.Save(file);
                    model.Institution="养老保险基金";await model.LoadTask;if(model.Rows.Count!=1||model.Rows[0].State!="NotListed")throw new Exception("机构筛选未保持未知持仓。");
                    model.Search="无此组合";await model.LoadTask;if(model.Rows.Count!=0||model.HoldingSeries.Length!=0||model.Disclosures.Count!=0)throw new Exception("空结果残留旧详情。");
                    Console.WriteLine("PASS 真实WPF直接持股：持仓点、组合筛选、空结果清理；截图为合成数据。");
                }
                catch(Exception error){failure=error;}
                finally{lifetime.Cancel();await workspace.DisposeAsync();window.Close();}
            };
            app.Run(window);
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw new InvalidOperationException("直接持股UI验收失败。",failure);
    }
}
