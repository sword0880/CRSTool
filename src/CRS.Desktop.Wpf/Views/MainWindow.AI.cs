using System.ComponentModel;
using System.Windows;
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

    private void CaptureAiPage(ShellViewModel shell)
    {
        if (aiLocked || shell.AiAssistant.Busy || aiPanel is null) return;
        if (!shell.AiAssistant.SupportsScreenshot)
        {
            shell.AiAssistant.Status = "当前模型无法接收截图，请先在设置中选择支持截图的模型。";
            return;
        }
        // 捕获导航内容或插件表面，避开独立的悬浮面板和操作系统桌面。
        var surface = PluginSurface.IsVisible ? PluginSurface :
            Navigation.SelectedItem is Wpf.Ui.Controls.NavigationViewItem { TargetPageType: { } pageType }
                ? pageProvider.FindCreatedPage(pageType) : null;
        try
        {
            if (surface is { IsVisible: true } && AiScreenshotCapture.Capture(surface) is { } image)
                aiPanel.SelectScreenshot(image);
            else shell.AiAssistant.Status = "当前页面尚未完成布局，请稍后再截取。";
        }
        catch (InvalidOperationException) { shell.AiAssistant.Status = "无法截取当前页面，可以粘贴截图。"; }
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
