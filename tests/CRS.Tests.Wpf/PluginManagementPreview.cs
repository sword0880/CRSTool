using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.Views.Pages;
using CRS.PluginHost;

internal static class PluginManagementPreview
{
    public static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                var app=new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
                var statuses=Enumerable.Range(0,80).Select(n=>new PluginStatus($"test.plugin.{n:000}",$"插件名称 {n:000}")).ToArray();
                var saves=new List<string>();
                Directory.CreateDirectory(directory);
                foreach(var width in new[]{1040,1280,1440})
                {
                    var page=new PluginManagementPage(statuses,(id,enabled)=>saves.Add(id+":"+enabled),_=>true);
                    var root=new Border{Background=(Brush)app.FindResource("BrandCanvasBrush"),Child=page};
                    root.Measure(new Size(width,740));root.Arrange(new Rect(0,0,width,740));root.UpdateLayout();
                    var grid=page.FindName("PluginTable") as DataGrid
                        ??throw new InvalidOperationException("插件管理缺少可操作的真实 DataGrid");
                    if(grid.Items.Count!=80 || grid.Columns.Count!=5 || !grid.IsReadOnly || grid.CanUserSortColumns ||
                        !grid.EnableRowVirtualization || grid.SelectionUnit!=DataGridSelectionUnit.FullRow)
                        throw new InvalidOperationException("插件管理表格失去只读/禁排序/虚拟化/整行选择契约");
                    if(!VirtualizingPanel.GetIsVirtualizing(grid) || VirtualizingPanel.GetVirtualizationMode(grid)!=VirtualizationMode.Recycling)
                        throw new InvalidOperationException("插件表格行回收虚拟化未生效");
                    grid.SelectedIndex=0;
                    if(grid.SelectedItem!=page.Rows[0])throw new InvalidOperationException("插件管理表格失去行选择交互");
                    if(width==1040)
                    {
                        page.Rows[0].ToggleCommand.Execute(null);
                        if(page.Rows[0].IsNextEnabled || saves.Count!=1 || !saves[0].EndsWith(":False",StringComparison.Ordinal))
                            throw new InvalidOperationException("插件启动选项未透传到原保存回调");
                    }
                    var bitmap=new RenderTargetBitmap(width,740,96,96,PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output=File.Create(Path.Combine(directory,$"plugin-management-{width}.png"));
                    encoder.Save(output);
                }
                Console.WriteLine("PASS 插件管理：80行合成状态、只读与禁排序、行回收、整行选择、保存回调及三种宽度截图");
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw failure;
    }
}
