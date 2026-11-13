using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Pages.AI;

/// <summary>从历史恢复现有悬浮助手，删除前确认并解除已打开的同一会话。</summary>
public partial class AiChatHistoryPage : Page
{
    private readonly ShellViewModel shell;

    public AiChatHistoryPage(ShellViewModel shell)
    {
        this.shell = shell;
        InitializeComponent();
        DataContext = shell.AiHistory;
        Loaded += async (_, _) => await shell.AiHistory.RefreshAsync();
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow window || shell.AiHistory.Conversation is not { } conversation) return;
        if (shell.AiAssistant.CurrentId == conversation.Id || shell.AiAssistant.Resume(conversation)) window.OpenAiAssistant();
        else shell.AiHistory.Status = "助手正在回答，请等待当前请求完成。";
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (shell.AiHistory.Selected is not { } selected) return;
        if (shell.AiAssistant.CurrentId == selected.Id && shell.AiAssistant.Busy)
        {
            shell.AiHistory.Status = "请先结束当前请求，再删除此会话。";
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this), "删除此会话及保留的截图？", "删除聊天记录",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        // 删除异步进行时先清空当前对话，避免新问题同时追加到待删除记录。
        if (shell.AiAssistant.CurrentId == selected.Id && shell.AiAssistant.NewChatCommand.CanExecute(null))
            shell.AiAssistant.NewChatCommand.Execute(null);
        await shell.AiHistory.DeleteAsync(selected.Id);
    }
}
