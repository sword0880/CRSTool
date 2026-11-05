using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;

/// <summary>仅用合成服务检查独立数据库窗口；不打开真实数据库、不执行真实备份或恢复。</summary>
internal static class DatabaseBackupViewChecks
{
    public static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
            var vault=new VaultInteractionChecks.FakeVault{BackupCapacity=new(128*1024*1024,2L*1024*1024*1024)};
            vault.CompleteEnrollmentAsync(true).GetAwaiter().GetResult();
            var model=new DatabaseBackupViewModel(vault,new DatabaseBackupChecks.Settings(),"D:/Synthetic/CurrentDatabase")
            {BackupDestination="D:/Synthetic/Backups/current.crsbak",RestoreSource="D:/Synthetic/Backups/previous.crsbak",RestoreTarget="D:/Synthetic/RestoredDatabase"};
            var window=new DatabaseBackupWindow(model){ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None};
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    Directory.CreateDirectory(directory);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    if(window.Title!="数据库与备份管理"||window.DataContext is not DatabaseBackupViewModel)
                        throw new InvalidOperationException("管理入口未使用独立数据库窗口与模型。");
                    var forbidden=new[]{"手机验证码","验证并解锁","进入工作区","更换手机","已有加密保险库"};
                    if(Descendants(window).OfType<TextBlock>().Any(x=>forbidden.Any(text=>x.Text.Contains(text))))
                        throw new InvalidOperationException("数据库窗口仍混有登录或手机验证内容。");
                    var close=Descendants(window).OfType<Button>().Single(x=>Equals(x.Content,"关闭"));
                    foreach(var size in new[]{(Width:560d,Height:680d),(Width:760d,Height:820d),(Width:1040d,Height:820d)})
                    {
                        window.Width=size.Width;window.Height=size.Height;window.UpdateLayout();
                        if(close.TranslatePoint(new Point(0,close.ActualHeight),window).Y>window.ActualHeight)
                            throw new InvalidOperationException("关闭按钮被滚动内容挤出窗口。");
                        Capture(window,Path.Combine(directory,$"database-backup-{size.Width:0}.png"));
                    }
                    window.Width=760;Descendants(window).OfType<ScrollViewer>().First().ScrollToEnd();window.UpdateLayout();
                    Capture(window,Path.Combine(directory,"database-restore-760.png"));
                    var secret=(PasswordBox)window.FindName("RestoreSecretInput");var password=(PasswordBox)window.FindName("RestorePasswordInput");
                    secret.Password="synthetic-secret";password.Password="synthetic-new-password";
                    await model.BackupCommand.ExecuteAsync(null);
                    if(vault.BackupRequests.Count!=1||secret.Password.Length!=0||password.Password.Length!=0)
                        throw new InvalidOperationException("备份命令未接通或操作后恢复凭证未清理。");
                    vault.BeforeBackup=token=>Task.Delay(Timeout.Infinite,token);
                    secret.Password="synthetic-secret";password.Password="synthetic-new-password";
                    var pending=model.BackupCommand.ExecuteAsync(null);window.AbortForLock();await pending;
                    if(model.IsBusy||model.RestoreSecret.Length!=0||model.NewPassword.Length!=0||secret.Password.Length!=0||password.Password.Length!=0)
                        throw new InvalidOperationException("锁定未取消数据库操作或清空凭证。");
                }
                catch(Exception error){failure=error;}
                finally{window.Close();app.Shutdown();}
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("独立数据库窗口验收失败。",failure);
        Console.WriteLine("PASS 独立数据库窗口：无登录/手机验证内容，560/760/1040 DIP布局、独立备份接线及锁定取消/凭证清理；4张合成渲染。");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    private static void Capture(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
}
