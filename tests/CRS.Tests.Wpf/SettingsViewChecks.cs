using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.AI.Models;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.DesktopClient.Views.AI;
using CRS.DesktopClient.Views.Pages.AI;
using CRS.DesktopClient.Views.Settings;
using CRS.DesktopClient.Views.Settings.AI;
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
            var model=new SettingsViewModel(new WorkspaceState(new SettingsUseCases(),new SilentInteraction()),new Settings(),vault,cleanupAudit is null?workerLogs:new DataCleanupViewChecks.PreviewGateway(cleanupAudit),new ModelService());
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
                            "ai-models"=>"AiModelSection",
                            "notifications"=>"NotificationsSection",
                            "dingtalk"=>"DingTalkSection",
                            "wechat"=>"WeChatSection",
                            "logging"=>"LoggingSection",
                            _=>throw new Exception("出现未登记的设置页面："+section.Key)
                        };
                        if(window.FindName(viewName) is not FrameworkElement panel || !panel.IsVisible)
                            throw new Exception("设置子视图未随目录显示："+section.Key);
                        // SettingsSectionHost 只折叠非当前分类，子控件必须保留同一实例与草稿。
                        foreach(var other in new[]{"StorageSection","CleanupSection","SecuritySection","DocumentsSection","AiModelSection","NotificationsSection","DingTalkSection","WeChatSection","LoggingSection"})
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
                    if(model.NavigationGroups.Count!=5||model.NavigationGroups.Single(x=>x.Title=="安全").Children.Count!=2)throw new Exception("清空搜索后未恢复分层目录。");
                    await VerifyAiModelsAsync(window, model, directory);
                    if (cleanupAudit is null) await VerifyAiAssistantAsync(directory);
                    model.SelectedSection=model.Sections[0];
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
                    var aiKey=(PasswordBox)((AiModelSettingsView)window.FindName("AiModelSection")).FindName("ApiKeyInput");aiKey.Password="synthetic-ai-lock";
                    model.SelectedSection=model.Sections[0];
                    if(dingToken.Password.Length==0||wxToken.Password.Length==0)throw new Exception("切换分类丢失通知Token草稿。");
                    model.SectionSearch="微信";
                    if(model.SelectedSection?.Key!="wechat"||model.VisibleSections.Count!=1)throw new Exception("微信搜索没有定位个人微信分类。");
                    model.State.IsBusy=true;
                    window.AbortForLock();
                    if(password.Password.Length!=0||model.DocumentPassword.Length!=0||model.SecurityPassword.Length!=0||securityPassword.Password.Length!=0||securityCode.Password.Length!=0||documentPassword.Password.Length!=0||documentConfirmation.Password.Length!=0||dingToken.Password.Length!=0||dingSecret.Password.Length!=0||wxToken.Password.Length!=0||aiKey.Password.Length!=0)throw new Exception("锁定设置窗口未清空凭证。");
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

    // 通过真实选择器和按钮验证默认模型保存、密码框清空及两项诊断相互独立。
    private static async Task VerifyAiModelsAsync(SettingsWindow window, SettingsViewModel model, string directory)
    {
        model.SectionSearch = "DeepSeek";
        var view = (AiModelSettingsView)window.FindName("AiModelSection");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (model.SelectedSection?.Key != "ai-models" || !view.IsVisible) throw new Exception("AI 模型页无法检索。");
        var provider = (ComboBox)view.FindName("ProviderInput");
        var models = (ComboBox)view.FindName("ModelInput");
        var key = (PasswordBox)view.FindName("ApiKeyInput");
        // 两家新增供应商应出现在同一选择器，并同步自己的模型列表。
        foreach (var (id, firstModel) in new[] { ("mimo", "mimo-v2.6-flash"), ("anthropic", "claude-haiku-5-5") })
        {
            provider.SelectedItem = model.AiModels.Providers.Single(item => item.Id == id);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if ((string?)models.SelectedItem != firstModel) throw new Exception("供应商没有加载对应的默认型号。");
        }
        key.Password = "previous-provider-draft";
        provider.SelectedIndex = 1;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (key.Password.Length != 0 || model.AiModels.SelectedModel != "gpt-5.4-mini" ||
            (string?)models.SelectedItem != "gpt-5.4-mini") throw new Exception("切换供应商未更新模型并清空旧密钥。");
        models.SelectedIndex = 1;
        key.Password = "synthetic-openai";
        var button = (Wpf.Ui.Controls.Button)view.FindName("TestAndSaveButton");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (model.AiModels.DefaultLabel != "OpenAI · gpt-5-mini" || key.Password.Length != 0 ||
            model.AiModels.ModelStatus != "连接成功" || !model.AiModels.GatewayStatus.Contains("无法连接 Gateway"))
            throw new Exception("模型保存、独立诊断或密码清理失败。");
        Capture(window, Path.Combine(directory, "ai-models.png"));
        model.SectionSearch = "";
        Console.WriteLine("PASS AI 模型设置的真实 WPF 选择、测试、保存和凭证清理。");
    }

    private sealed class ModelService : IAiModelService
    {
        private AiModelSettings settings = new(null, new Dictionary<string, AiProviderSetting>());
        public AiModelSettings LoadSettings() => settings;
        public Task<AiModelSettings> SaveDefaultAsync(string provider, string model, string? newKey, CancellationToken token) =>
            Task.FromResult(settings = new(provider, new Dictionary<string, AiProviderSetting> { [provider] = new(model, true) }));
        public Task TestModelAsync(string provider, string model, string? newKey, CancellationToken token) =>
            newKey == "synthetic-openai" ? Task.CompletedTask : Task.FromException(new AiModelException("InvalidApiKey"));
        public Task TestGatewayAsync(CancellationToken token) => Task.FromException(new AiModelException("GatewayUnavailable"));
        public Task<AiModelSettings> RemoveKeyAsync(string provider, CancellationToken token) => throw new NotSupportedException();
        public Task<AiModelAnswer> AskDefaultAsync(string expectedProvider, string expectedModel, string prompt, CancellationToken token)
        {
            if (settings.DefaultProvider != expectedProvider ||
                settings.Providers.GetValueOrDefault(expectedProvider)?.Model != expectedModel)
                throw new AiModelException("DefaultModelChanged");
            if (prompt == "等待取消") return WaitForCancellationAsync(token);
            return Task.FromResult(new AiModelAnswer("合成 ETF 研究结果", false, new(12, 4, 16), 2, 1));
        }

        public AiChatRequest? LastChat { get; private set; }

        public Task<AiModelAnswer> AskChatAsync(string expectedProvider, string expectedModel, AiChatRequest request, CancellationToken token)
        {
            LastChat = request;
            return AskDefaultAsync(expectedProvider, expectedModel, request.Prompt, token);
        }

        private static async Task<AiModelAnswer> WaitForCancellationAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("测试应先取消请求。");
        }
    }

    // 验证正式侧栏路由、回答呈现及锁定后晚到请求无法恢复界面内容。
    private static async Task VerifyAiAssistantAsync(string directory)
    {
        var brandAccent = (SolidColorBrush)System.Windows.Application.Current.FindResource("BrandAccentBrush");
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(brandAccent.Color, Wpf.Ui.Appearance.ApplicationTheme.Light);
        var service = new ModelService();
        await service.SaveDefaultAsync("deepseek", "deepseek-flash", "synthetic", default);
        var history = new ChatHistory();
        var shell = new ShellViewModel(new SettingsUseCases(), new SilentInteraction(), new Settings(), aiModels: service, aiHistory: history);
        var window = new MainWindow(shell, startWorkspace: false);
        window.Show();
        try
        {
            if (!window.Navigate("dashboard")) throw new Exception("无法打开概览。");
            var launcher = (Wpf.Ui.Controls.Button)window.FindName("AiAssistantLauncher");
            var dock = (Border)window.FindName("AiAssistantDock");
            window.UpdateLayout();
            if (dock.IsVisible || launcher.Content is not null || launcher.Icon is null ||
                launcher.ActualWidth > 36 || launcher.ActualHeight > 36)
                throw new Exception("助手收起时应只显示紧凑的图标入口。");
            launcher.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var surface = (Border)window.FindName("PluginSurface");
            if (dock.Visibility != Visibility.Visible || Panel.GetZIndex(dock) <= Panel.GetZIndex(surface))
                throw new Exception("助手没有显示在业务页面上层。");
            var panel = (AiAssistantPanel)((ContentControl)window.FindName("AiAssistantDockContent")).Content;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            // 触发真实页面捕获并在模态窗口打开后取消，验证边界与取消结果。
            var capture = (Wpf.Ui.Controls.Button)panel.FindName("CaptureButton");
            var cropOpened = false;
            capture.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var captureDeadline = Environment.TickCount64 + 5000;
            while (!dock.IsVisible && Environment.TickCount64 < captureDeadline)
            {
                if (window.OwnedWindows.OfType<ScreenshotCropWindow>().SingleOrDefault() is { IsVisible: true } crop)
                {
                    cropOpened = true;
                    crop.Close();
                }
                await Task.Delay(25);
            }
            if (!cropOpened || !dock.IsVisible || shell.AiAssistant.HasScreenshot)
                throw new Exception("页面截图未进入选区，或取消后留下附件。" + shell.AiAssistant.Status);
            // 使用真正编码的合成图验证预览与保留策略，不发送到供应商。
            var preview = new RenderTargetBitmap(64, 32, 96, 96, PixelFormats.Pbgra32);
            var region = new ScreenshotCropWindow(preview);
            var selected = region.Crop(new Rect(30, 30, 50, 25), new Rect(10, 20, 100, 50));
            if (selected.PixelWidth != 33 || selected.PixelHeight != 17) throw new Exception("截图选区像素换算失败。");
            region.Close();
            var jpeg = new JpegBitmapEncoder();
            jpeg.Frames.Add(BitmapFrame.Create(preview));
            using var screenshot = new MemoryStream();
            jpeg.Save(screenshot);
            shell.AiAssistant.AttachScreenshot(screenshot.ToArray());
            shell.AiAssistant.Prompt = "公开 ETF 概览";
            await shell.AiAssistant.AskCommand.ExecuteAsync(null);
            if (service.LastChat?.Screenshot is null || history.Current?.Entries.Single().Screenshot is not null ||
                shell.AiAssistant.HasScreenshot)
                throw new Exception("截图未随本条发送，或默认保留了截图。");
            var detach = (Wpf.Ui.Controls.Button)panel.FindName("DetachButton");
            detach.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (!window.OwnedWindows.Cast<Window>().Any(item => item.Topmost && ReferenceEquals(item.Content, panel)))
                throw new Exception("弹出后没有置顶或未复用助手内容。");
            detach.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (!window.Navigate("ai-history")) throw new Exception("无法打开 AI 聊天历史。");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var navigation = (Wpf.Ui.Controls.NavigationView)window.FindName("Navigation");
            if (navigation.SelectedItem is not Wpf.Ui.Controls.NavigationViewItem { TargetPageType: var page } ||
                page != typeof(AiChatHistoryPage) || !dock.IsVisible || shell.AiAssistant.Entries.Count != 1 ||
                shell.AiHistory.Items.Count != 1)
                throw new Exception("切页丢失助手或聊天历史未接入菜单。");
            var historyPage = FindVisual<AiChatHistoryPage>(navigation) ?? throw new Exception("聊天历史页面未显示。");
            var historyList = (ListBox)historyPage.FindName("HistoryList");
            var search = (Wpf.Ui.Controls.TextBox)historyPage.FindName("HistorySearch");
            var older = new AiChatSummary(Guid.NewGuid(), "按券商梳理年度交易与资金流水，核对港股通和基金持仓变化", DateTimeOffset.UtcNow.AddDays(-1), "gpt-5-mini");
            history.Older =
            [
                older,
                new(Guid.NewGuid(), "核对上一年度股息与预扣税", DateTimeOffset.UtcNow.AddDays(-2), "gpt-5-mini"),
                new(Guid.NewGuid(), "整理美股交易的汇率差异", DateTimeOffset.UtcNow.AddDays(-3), "gpt-5-mini")
            ];
            await shell.AiHistory.RefreshAsync();
            historyList.SelectedIndex = 0;
            window.UpdateLayout();
            var selectedRow = (ListBoxItem)historyList.ItemContainerGenerator.ContainerFromIndex(0);
            var selectedSurface = (Border)selectedRow.Template.FindName("RowSurface", selectedRow);
            if (!Equals(selectedSurface.Background, window.FindResource("ControlAltFillColorTertiaryBrush")) ||
                !Equals(selectedRow.Foreground, window.FindResource("BrandTextPrimaryBrush")) ||
                shell.AiHistory.Conversation?.Id != history.Current?.Id)
                throw new Exception("选中会话未使用中性背景、深色文字，或未加载聊天记录。");
            var olderRow = (ListBoxItem)historyList.ItemContainerGenerator.ContainerFromIndex(1);
            var olderTitle = FindVisual<TextBlock>(olderRow);
            if (olderRow.ActualHeight > 38 || olderTitle?.TextTrimming != TextTrimming.CharacterEllipsis ||
                !Equals(olderTitle.ToolTip, older.Title))
                throw new Exception("最近聊天标题未按单行截断或未保留完整提示。");
            launcher.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (dock.IsVisible) throw new Exception("点击助手图标未收起面板。");
            VerifyTranscriptColors(historyPage);
            Capture(window, Path.Combine(directory, "ai-history.png"));
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Capture(window, Path.Combine(directory, "ai-history-compact.png"));
            var menuButton = FindVisual<Wpf.Ui.Controls.Button>(olderRow) ?? throw new Exception("会话操作按钮缺失。");
            menuButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var menu = menuButton.ContextMenu;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!menu.IsOpen || ((MenuItem)menu.Items[0]).DataContext is not AiChatSummary target || target.Id != older.Id)
                throw new Exception("会话菜单未绑定打开菜单的历史项。");
            menu.IsOpen = false;
            if (!await shell.AiHistory.DeleteAsync(older.Id) || history.Older.Any(item => item.Id == older.Id) ||
                shell.AiHistory.Selected?.Id != history.Current?.Id || shell.AiHistory.Conversation?.Id != history.Current?.Id)
                throw new Exception("删除其他会话时丢失当前选中的聊天。");
            search.SetCurrentValue(TextBox.TextProperty, "合成 ETF 研究结果");
            var searchDeadline = Environment.TickCount64 + 5000;
            while (historyList.Items.Count != 1 && Environment.TickCount64 < searchDeadline) await Task.Delay(25);
            if (historyList.Items.Count != 1 || ((AiChatSummary)historyList.Items[0]).Id != history.Current?.Id)
                throw new Exception("最近聊天搜索未找到回答中的关键词。");
            historyList.SelectedIndex = 0;
            var continueChat = (Wpf.Ui.Controls.Button)historyPage.FindName("ContinueChatButton");
            if (!continueChat.IsEnabled) throw new Exception("选中会话后无法继续对话。");
            continueChat.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if (!dock.IsVisible) throw new Exception("继续对话未打开助手。");
            shell.AiAssistant.AttachScreenshot(screenshot.ToArray());
            shell.AiAssistant.KeepScreenshot = true;
            shell.AiAssistant.Prompt = "继续问";
            await shell.AiAssistant.AskCommand.ExecuteAsync(null);
            if (service.LastChat?.History.Count != 1 || history.Current?.Entries.Last().Screenshot is null)
                throw new Exception("继续聊天丢失文字上下文，或未保存明确勾选的截图。");
            if (shell.AiAssistant.DefaultModelLabel != "DeepSeek · deepseek-flash" ||
                shell.AiAssistant.Answer != "合成 ETF 研究结果" || !shell.AiAssistant.Usage.Contains("工具调用 1 次"))
                throw new Exception("研究助手未使用当前模型或没有显示回答。");
            VerifyTranscriptColors(panel);
            Capture(window, Path.Combine(directory, "ai-assistant.png"));
            shell.AiAssistant.Prompt = "等待取消";
            var inFlight = shell.AiAssistant.AskCommand.ExecuteAsync(null);
            window.PrepareLock();
            await inFlight;
            if (shell.AiAssistant.Prompt.Length != 0 || shell.AiAssistant.Answer.Length != 0 ||
                shell.AiAssistant.Status.Length != 0 || shell.AiAssistant.Busy)
                throw new Exception("锁定未取消并清理 AI 问答。");
            Console.WriteLine("PASS AI 助手跨页悬浮、独立置顶、截图保留、聊天历史和锁定取消。");
        }
        finally { window.Close(); }
    }

    private static void VerifyTranscriptColors(FrameworkElement view)
    {
        var transcript = FindVisual<AiChatTranscript>(view) ?? throw new Exception("聊天消息控件缺失。");
        var messages = (ListBox)transcript.FindName("MessageList");
        messages.SelectedIndex = 0;
        view.UpdateLayout();
        var row = (ListBoxItem)messages.ItemContainerGenerator.ContainerFromIndex(0);
        var surface = (Border)row.Template.FindName("Border", row);
        var message = FindVisual<TextBox>(row) ?? throw new Exception("问答正文缺失。");
        if (!Equals(surface.Background, view.FindResource("ControlAltFillColorTertiaryBrush")) ||
            !Equals(row.Foreground, view.FindResource("BrandTextPrimaryBrush")) ||
            !Equals(message.SelectionBrush, view.FindResource("ControlAltFillColorQuarternaryBrush")))
            throw new Exception("聊天消息行和正文选中时应显示中性背景。");
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
        return null;
    }

    private sealed class ChatHistory : IAiChatHistory
    {
        public AiChatConversation? Current { get; private set; }
        public IReadOnlyList<AiChatSummary> Older { get; set; } = [];

        public Task<IReadOnlyList<AiChatSummary>> SearchAsync(string query, CancellationToken token)
        {
            var matches = Older.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (Current is { } chat && chat.Entries.Any(entry => entry.Question.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Answer.Contains(query, StringComparison.OrdinalIgnoreCase)))
                matches = matches.Prepend(new(chat.Id, chat.Entries[0].Question, DateTimeOffset.UtcNow, chat.Model));
            return Task.FromResult<IReadOnlyList<AiChatSummary>>(matches.ToArray());
        }

        public Task<AiChatConversation?> ReadAsync(Guid id, CancellationToken token) => Task.FromResult(Current?.Id == id ? Current : null);

        public Task<long> AppendAsync(Guid id, string provider, string model, long revision, AiChatEntry entry, CancellationToken token)
        {
            Current = new(id, provider, model, revision + 1, [.. Current?.Entries ?? [], entry]);
            return Task.FromResult(revision + 1);
        }

        public Task DeleteAsync(Guid id, CancellationToken token)
        {
            if (Current?.Id == id) Current = null;
            Older = Older.Where(item => item.Id != id).ToArray();
            return Task.CompletedTask;
        }
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
