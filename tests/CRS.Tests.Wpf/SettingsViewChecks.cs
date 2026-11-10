using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.DesktopClient.Views.Settings;
using CRS.Maintenance;

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
            var workerLogs=new WorkerLogs();
            var model=new SettingsViewModel(new WorkspaceState(new SettingsUseCases(),new SilentInteraction()),new Settings(),vault,cleanupAudit is null?workerLogs:new DataCleanupViewChecks.PreviewGateway(cleanupAudit));
            var window=new SettingsWindow(model);
            if(window.FindName("LoggingSection") is not LoggingSettingsView)
                throw new InvalidOperationException("日志设置未采用独立子视图。");
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    await VerifyLoginSelectionAsync(window,model,vault);
                    if(cleanupAudit is null)
                    {
                        model.SelectedSection=model.Sections.Single(x=>x.Key=="logging");
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        var logging=(LoggingSettingsView)window.FindName("LoggingSection");
                        var retention=(Wpf.Ui.Controls.NumberBox)logging.FindName("WorkerLogRetentionBox");
                        if(!retention.IsEnabled||retention.Value!=14)throw new Exception("前台没有读取后台日志保留天数。");
                        retention.SetCurrentValue(Wpf.Ui.Controls.NumberBox.ValueProperty,30d);
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        if(model.WorkerLogRetentionDays!=30||!model.SaveCommand.CanExecute(null))
                            throw new Exception($"前台输入未提交，保留天数={model.WorkerLogRetentionDays}，可以保存={model.SaveCommand.CanExecute(null)}。");
                        await model.SaveCommand.ExecuteAsync(null);
                        if(workerLogs.Settings!=new WorkerLogSettings(30,1)||!model.WorkerLogSettingsMessage.Contains("30 天"))
                            throw new Exception("保存设置没有更新后台日志保留天数。"+model.WorkerLogSettingsMessage);
                        Console.WriteLine("PASS 前台可读取并保存后台日志保留天数。");
                    }
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
    private static async Task VerifyLoginSelectionAsync(SettingsWindow window,SettingsViewModel model,VaultInteractionChecks.FakeVault vault)
    {
        var security=(SecuritySettingsView)window.FindName("SecuritySection");
        var hello=(RadioButton)security.FindName("WindowsHelloOption");var phone=(RadioButton)security.FindName("PhoneVerificationOption");
        if(!hello.Command.CanExecute(hello.CommandParameter)||!phone.Command.CanExecute(phone.CommandParameter))throw new Exception("合成授权会话没有开放登录方式选择。");
        phone.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if(model.WindowsHelloEnabled)throw new Exception("组内取消选中意外反向修改登录方式。");
        for(var i=0;i<32;i++)
        {
            model.SelectedSection=model.Sections.Single(x=>x.Key=="security");
            hello.Command.Execute(hello.CommandParameter);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if(!model.WindowsHelloEnabled||model.PhoneVerificationEnabled||hello.IsChecked!=true||phone.IsChecked==true)throw new Exception("Hello选择没有稳定同步到真实单选控件。");
            phone.Command.Execute(phone.CommandParameter);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if(model.WindowsHelloEnabled||!model.PhoneVerificationEnabled||phone.IsChecked!=true||hello.IsChecked==true)throw new Exception("手机选择没有稳定同步到真实单选控件。");
            model.SelectedSection=model.Sections.Single(x=>x.Key=="cleanup");
            model.WindowsHelloEnabled=true;model.Reload();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if(model.WindowsHelloEnabled||!model.PhoneVerificationEnabled||vault.UsesWindowsHello)throw new Exception("隐藏的验证设置在清理页面刷新时改变了已保存登录方式。");
        }
        await vault.SetLoginMethodAsync(true,"synthetic-master","123456");model.Reload();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if(!model.WindowsHelloEnabled||model.PhoneVerificationEnabled||hello.IsChecked!=true)throw new Exception("已保存Hello的会话刷新时发生错误切换。");
        await vault.SetLoginMethodAsync(false,"synthetic-master","123456");model.Reload();
        model.SelectedSection=model.Sections[0];
        Console.WriteLine("PASS 登录方式：真实单选控件32轮切换、清理页面中的刷新、组内取消选中均无递归；未保存时不修改验证方式。");
    }
    private sealed class Settings:IDesktopSettingsService
    {
        public DesktopSettings Load()=>new("D:\\CRS\\data","D:\\CRS\\logs");
        public Task SaveAsync(DesktopSettings settings)=>Task.CompletedTask;
    }
    private sealed class WorkerLogs:IDataCleanupGateway,IWorkerLogSettingsGateway
    {
        public bool CanExecute=>true;
        public WorkerLogSettings Settings {get;private set;}=new();
        public Task<WorkerLogSettings> LoadLogSettingsAsync(CancellationToken token)=>Task.FromResult(Settings);
        public Task<WorkerLogSettings> SaveLogSettingsAsync(WorkerLogSettings settings,CancellationToken token)
            =>Task.FromResult(Settings=settings with{Revision=settings.Revision+1});
        public Task<DataCleanupStatus> PreviewAsync(DataCleanupOptions options,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>throw new NotSupportedException();
        public Task<DataCleanupStatus> ExecuteAsync(Guid previewId,Guid executionId,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>throw new NotSupportedException();
        public Task<DataCleanupStatus> StatusAsync(Guid operationId,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>throw new NotSupportedException();
    }
    // 保留完整配置表单交互，所有地址为不可投递的保留域；发送接口仍没有实现。
    private sealed class SettingsUseCases:FakeUseCases,IDesktopUseCases
    {
        public Task<NotificationWebhookSettings> LoadWebhookSettingsAsync(string channel,CancellationToken token=default)=>Task.FromResult(new NotificationWebhookSettings(channel,Revision:1));
        public Task<NotificationEmailSettings> LoadEmailSettingsAsync(CancellationToken token=default)=>
            Task.FromResult(new NotificationEmailSettings(false,"smtp.example.invalid",587,"StartTls","research@example.invalid","owner@example.invalid","",false,1));
    }
}
