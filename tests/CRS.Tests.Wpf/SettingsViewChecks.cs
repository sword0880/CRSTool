using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.DesktopClient.Views.Settings;

/// <summary>使用隔离设置替身渲染真实窗口，不访问个人数据库或发送通知。</summary>
internal static class SettingsViewChecks
{
    public static void Run(string directory,string? cleanupAudit=null)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var app=new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
            app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
            var vault=new VaultInteractionChecks.FakeVault{HelloSupported=true};vault.CompleteEnrollmentAsync(true).GetAwaiter().GetResult();
            var model=new SettingsViewModel(new WorkspaceState(new SettingsUseCases(),new SilentInteraction()),new Settings(),vault,cleanupAudit is null?null:new DataCleanupViewChecks.PreviewGateway(cleanupAudit));
            var window=new SettingsWindow(model);
            if(window.FindName("LoggingSection") is not LoggingSettingsView)
                throw new InvalidOperationException("日志设置未采用独立子视图。");
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Directory.CreateDirectory(directory);Capture(window,Path.Combine(directory,"settings.png"));
                    var tree=(TreeView)window.FindName("SectionNavigation");tree.UpdateLayout();
                    var group=(TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(1);group.UpdateLayout();
                    var leaf=(TreeViewItem)group.ItemContainerGenerator.ContainerFromIndex(1);leaf.IsSelected=true;
                    if(model.SelectedSection?.Key!="documents")throw new Exception("树目录选择没有切换到对应设置页。");
                    model.DatabaseDirectory="D:\\draft-data";
                    var documents=(DocumentSettingsView)window.FindName("DocumentsSection");
                    var documentPassword=(PasswordBox)documents.FindName("DocumentPasswordInput");documentPassword.Password="synthetic-document-only";
                    foreach(var section in model.Sections)
                    {
                        model.SelectedSection=section;
                        if(section.Key=="cleanup"&&cleanupAudit is not null){model.Cleanup.IncludeBackups=true;await model.Cleanup.ScanCommand.ExecuteAsync(null);}
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        var viewName=section.Key switch
                        {
                            "storage"=>"StorageSection",
                            "cleanup"=>"CleanupSection",
                            "security"=>"SecuritySection",
                            "documents"=>"DocumentsSection",
                            "notifications"=>"NotificationsSection",
                            "dingtalk"=>"DingTalkSection",
                            "wechat"=>"WeChatSection",
                            "logging"=>"LoggingSection",
                            _=>throw new Exception("出现未登记的设置页面："+section.Key)
                        };
                        if(window.FindName(viewName) is not FrameworkElement panel || !panel.IsVisible)
                            throw new Exception("设置子视图未随目录显示："+section.Key);
                        // SettingsSectionHost 只折叠非当前分类，子控件必须保留同一实例与草稿。
                        foreach(var other in new[]{"StorageSection","CleanupSection","SecuritySection","DocumentsSection","NotificationsSection","DingTalkSection","WeChatSection","LoggingSection"})
                            if(other!=viewName && window.FindName(other) is FrameworkElement hidden && hidden.IsVisible)
                                throw new Exception("非当前设置分类仍显示："+other);
                        Capture(window,Path.Combine(directory,section.Key+".png"));
                    }
                    model.SectionSearch="smtp";
                    if(model.SelectedSection?.Key!="notifications"||model.VisibleSections.Count!=1)throw new Exception("SMTP关键词未定位通知分类。");
                    model.SectionSearch="不存在的设置";
                    if(!model.NoSectionMatches||model.SelectedSection is not null)throw new Exception("搜索零匹配仍显示旧分类。");
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Capture(window,Path.Combine(directory,"empty.png"));
                    model.SectionSearch="";model.SelectedSection=model.Sections[0];
                    if(model.DatabaseDirectory!="D:\\draft-data")throw new Exception("切换分类丢失未保存输入。");
                    if(model.DocumentPassword!="synthetic-document-only"||documentPassword.Password!=model.DocumentPassword)throw new Exception("切换分类意外清空密码草稿。");
                    if(model.NavigationGroups.Count!=4||model.NavigationGroups.Single(x=>x.Title=="安全").Children.Count!=2)throw new Exception("清空搜索后未恢复分层目录。");
                    // 最小尺寸必须保留内容视口与固定保存按钮，不能依赖长页滚动才能保存。
                    window.Width=window.MinWidth;window.Height=window.MinHeight;window.UpdateLayout();
                    var save=(FrameworkElement)window.FindName("SaveSettingsButton");
                    var viewport=(ScrollViewer)window.FindName("SectionContentScroll");
                    if(save.TranslatePoint(new Point(0,save.ActualHeight),window).Y>window.ActualHeight||viewport.ActualHeight<150)throw new Exception("最小设置窗口遮挡操作或正文。");
                    Capture(window,Path.Combine(directory,"compact.png"));
                    var security=(SecuritySettingsView)window.FindName("SecuritySection");
                    var securityPassword=(PasswordBox)security.FindName("SecurityPasswordInput");securityPassword.Password="synthetic-master";
                    var securityCode=(PasswordBox)security.FindName("SecurityCodeInput");securityCode.Password="123456";
                    var documentConfirmation=(PasswordBox)documents.FindName("DocumentConfirmationInput");documentConfirmation.Password="synthetic-document-only";
                    var password=(PasswordBox)((EmailSettingsView)window.FindName("NotificationsSection")).FindName("EmailPasswordInput");password.Password="synthetic-only";
                    var dingTalk=(DingTalkSettingsView)window.FindName("DingTalkSection");
                    var dingToken=(PasswordBox)dingTalk.FindName("DingTalkToken");dingToken.Password="syntheticDingToken";
                    var dingSecret=(PasswordBox)dingTalk.FindName("DingTalkSecret");dingSecret.Password="SECsyntheticSecret";
                    var wxToken=(PasswordBox)((WeChatSettingsView)window.FindName("WeChatSection")).FindName("WeChatToken");wxToken.Password="syntheticWeChatToken";
                    model.SelectedSection=model.Sections[0];
                    if(dingToken.Password.Length==0||wxToken.Password.Length==0)throw new Exception("切换分类丢失通知Token草稿。");
                    model.SectionSearch="微信";
                    if(model.SelectedSection?.Key!="wechat"||model.VisibleSections.Count!=1)throw new Exception("微信搜索没有定位个人微信分类。");
                    model.State.IsBusy=true;
                    window.AbortForLock();
                    if(password.Password.Length!=0||model.DocumentPassword.Length!=0||model.SecurityPassword.Length!=0||securityPassword.Password.Length!=0||securityCode.Password.Length!=0||documentPassword.Password.Length!=0||documentConfirmation.Password.Length!=0||dingToken.Password.Length!=0||dingSecret.Password.Length!=0||wxToken.Password.Length!=0)throw new Exception("锁定设置窗口未清空凭证。");
                }
                catch(Exception error){failure=error;}
                finally{window.Close();}
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw failure;
        Console.WriteLine("PASS 设置分类搜索、零匹配、草稿保留、最小窗口布局、锁定凭证清理与真实WPF渲染（隔离配置，无邮件投递）。");
    }
    private static void Capture(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
    private sealed class Settings:IDesktopSettingsService
    {
        public DesktopSettings Load()=>new("D:\\CRS\\data","D:\\CRS\\logs");
        public Task SaveAsync(DesktopSettings settings)=>Task.CompletedTask;
    }
    // 保留完整配置表单交互，所有地址为不可投递的保留域；发送接口仍没有实现。
    private sealed class SettingsUseCases:FakeUseCases,IDesktopUseCases
    {
        public Task<NotificationWebhookSettings> LoadWebhookSettingsAsync(string channel,CancellationToken token=default)=>Task.FromResult(new NotificationWebhookSettings(channel,Revision:1));
        public Task<NotificationEmailSettings> LoadEmailSettingsAsync(CancellationToken token=default)=>
            Task.FromResult(new NotificationEmailSettings(false,"smtp.example.invalid",587,"StartTls","research@example.invalid","owner@example.invalid","",false,1));
    }
}
