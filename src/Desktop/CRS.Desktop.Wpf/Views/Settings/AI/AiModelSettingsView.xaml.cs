using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels.AI;

namespace CRS.DesktopClient.Views.Settings.AI;

/// <summary>新密钥仅在点击测试时传给模型层，切换供应商和关闭窗口时清空输入。</summary>
public partial class AiModelSettingsView : UserControl, ISettingsSecretView
{
    public AiModelSettingsView() => InitializeComponent();

    public void ClearInputs() => ApiKeyInput?.Clear();

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e) => ClearInputs();

    private async void OnTestAndSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiModelSettingsViewModel model) return;
        var key = ApiKeyInput.Password.Trim();
        if (await model.TestAndSaveAsync(key.Length == 0 ? null : key)) ClearInputs();
    }

    private async void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiModelSettingsViewModel model) return;
        ClearInputs();
        await model.RemoveKeyCommand.ExecuteAsync(null);
    }
}
