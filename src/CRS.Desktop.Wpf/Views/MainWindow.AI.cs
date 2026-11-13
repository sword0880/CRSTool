using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.AI;

namespace CRS.DesktopClient.Views;

public partial class MainWindow
{
    private AiAssistantPanel? aiPanel;
    private Window? aiFloating;
    private bool aiLocked;

    private void InitializeAiAssistant(ShellViewModel shell)
    {
        aiPanel = new AiAssistantPanel { DataContext = shell.AiAssistant };
        aiPanel.CollapseRequested += (_, _) => CollapseAiAssistant();
        aiPanel.DetachRequested += (_, _) => ToggleAiFloating();
        aiPanel.CaptureRequested += (_, _) => CaptureAiPage(shell);
        AiAssistantDockContent.Content = aiPanel;
    }

    public void OpenAiAssistant()
    {
        if (aiLocked) return;
        if (aiFloating is { } floating) { floating.Show(); floating.Activate(); }
        else AiAssistantDock.Visibility = Visibility.Visible;
        aiPanel?.FocusPrompt();
    }

    private void OnAiLauncherClick(object sender, RoutedEventArgs e)
    {
        if (AiAssistantDock.Visibility == Visibility.Visible || aiFloating?.IsVisible == true) CollapseAiAssistant();
        else OpenAiAssistant();
    }

    private void CollapseAiAssistant()
    {
        AiAssistantDock.Visibility = Visibility.Collapsed;
        aiFloating?.Hide();
    }

    private void ToggleAiFloating()
    {
        if (aiLocked || aiPanel is null) return;
        if (aiFloating is { } previous)
        {
            // 切换父控件前释放可视树与逻辑树引用。
            previous.Content = null;
            aiFloating = null;
            previous.Closing -= OnAiFloatingClosing;
            previous.Close();
            AiAssistantDockContent.Content = aiPanel;
            aiPanel.SetDetached(false);
            OpenAiAssistant();
            return;
        }

        // 同一面板在停靠区与置顶窗口之间迁移，不重建聊天内容。
        CollapseAiAssistant();
        AiAssistantDockContent.Content = null;
        aiPanel.SetDetached(true);
        aiFloating = new Window
        {
            Owner = this, Content = aiPanel, Title = "小财牛助手", Topmost = true,
            Width = 470, Height = 650, MinWidth = 410, MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)FindResource("BrandSurfaceBrush")
        };
        aiFloating.Closing += OnAiFloatingClosing;
        OpenAiAssistant();
    }

    private void OnAiFloatingClosing(object? sender, CancelEventArgs e)
    {
        if (aiLocked || sender is not Window window) return;
        e.Cancel = true;
        window.Hide();
    }

    private async void CaptureAiPage(ShellViewModel shell)
    {
        if (aiLocked || shell.AiAssistant.Busy || aiPanel is null) return;
        if (!shell.AiAssistant.SupportsScreenshot)
        {
            shell.AiAssistant.Status = "当前模型无法接收截图，请先在设置中选择支持截图的模型。";
            return;
        }

        // 保存截图前的可见状态，保证取消、异常和锁定路径不会遗留隐藏的助手窗口。
        var previousDockVisibility = AiAssistantDock.Visibility;
        var previousLauncherVisibility = AiAssistantLauncher.Visibility;
        var floatingWindow = aiFloating;
        var floatingWasVisible = floatingWindow?.IsVisible == true;

        try
        {
            // 截图和选区裁剪期间隐藏助手，避免悬浮面板遮挡需要查看的页面内容。
            AiAssistantDock.Visibility = Visibility.Collapsed;
            AiAssistantLauncher.Visibility = Visibility.Collapsed;
            floatingWindow?.Hide();

            // 捕获导航内容或插件表面，不包含桌面及独立的助手窗口。
            var surface = PluginSurface.IsVisible ? PluginSurface :
                Navigation.SelectedItem is Wpf.Ui.Controls.NavigationViewItem { TargetPageType: { } pageType }
                    ? pageProvider.FindCreatedPage(pageType) : null;

            if (surface is not { IsVisible: true })
            {
                shell.AiAssistant.Status = "当前页面尚未完成布局，请稍后再截取。";
                return;
            }

            // 等待 WPF 完成隐藏后的布局/渲染调度，再捕获当前页面。
            var image = await Dispatcher.InvokeAsync(() =>
            {
                surface.UpdateLayout();
                return AiScreenshotCapture.Capture(surface);
            }, DispatcherPriority.Render);

            // 等待期间可能发生安全锁定；锁定后不得再打开截图裁剪窗口。
            if (aiLocked) return;

            if (image is null)
            {
                shell.AiAssistant.Status = "当前页面尚未完成布局，请稍后再截取。";
                return;
            }

            // 裁剪窗口由主窗口拥有；即使助手处于独立置顶状态也不会随其隐藏。
            aiPanel.SelectScreenshot(image, this);
        }
        catch (OperationCanceledException) when (aiLocked || Dispatcher.HasShutdownStarted)
        {
            // 锁定或窗口关闭时允许中止截图，不再恢复已清理的助手。
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            if (!aiLocked) shell.AiAssistant.Status = "无法截取当前页面，可以粘贴截图。";
        }
        finally
        {
            // 锁定流程已清理会话与窗口，不能在 finally 中重新显示助手。
            if (!aiLocked)
            {
                AiAssistantDock.Visibility = previousDockVisibility;
                AiAssistantLauncher.Visibility = previousLauncherVisibility;

                if (floatingWasVisible && floatingWindow is not null &&
                    ReferenceEquals(aiFloating, floatingWindow))
                {
                    floatingWindow.Show();
                    floatingWindow.Activate();
                }

                if (previousDockVisibility == Visibility.Visible || floatingWasVisible)
                    aiPanel?.FocusPrompt();
            }
        }
    }

    private void CloseAiForLock(ShellViewModel shell)
    {
        aiLocked = true;
        shell.AiAssistant.ClearForLock();
        shell.AiHistory.ClearForLock();
        aiPanel?.CloseCrop();
        if (aiFloating is { } floating)
        {
            floating.Content = null;
            floating.Close();
            aiFloating = null;
        }
        CollapseAiAssistant();
        AiAssistantLauncher.Visibility = Visibility.Collapsed;
    }
}
