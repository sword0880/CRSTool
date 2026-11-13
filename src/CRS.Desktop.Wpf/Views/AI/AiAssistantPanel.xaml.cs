using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CRS.DesktopClient.ViewModels.AI;

namespace CRS.DesktopClient.Views.AI;

/// <summary>主窗口和置顶窗口移动同一面板实例，输入和附件随对话保留。</summary>
public partial class AiAssistantPanel : UserControl
{
    private ScreenshotCropWindow? crop;
    public event EventHandler? CollapseRequested;
    public event EventHandler? DetachRequested;
    public event EventHandler? CaptureRequested;

    public AiAssistantPanel() => InitializeComponent();

    public void FocusPrompt() => Dispatcher.BeginInvoke(() => PromptInput.Focus());
    public void SetDetached(bool detached)
    {
        var action = detached ? "停靠回主窗口" : "弹出置顶窗口";
        DetachButton.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = detached ? Wpf.Ui.Controls.SymbolRegular.PanelRight20 : Wpf.Ui.Controls.SymbolRegular.WindowNew20
        };
        DetachButton.ToolTip = action;
        AutomationProperties.SetName(DetachButton, action);
    }
    public void CloseCrop() => crop?.Close();

    private void OnCollapse(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);
    private void OnDetach(object sender, RoutedEventArgs e) => DetachRequested?.Invoke(this, EventArgs.Empty);
    private void OnCapture(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke(this, EventArgs.Empty);

    private async void OnAttachFiles(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantViewModel { CanEditDraft: true } model) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择发送给小财牛的文本附件",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "支持的文本附件 (*.txt;*.md;*.csv;*.json)|*.txt;*.md;*.csv;*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await model.AddAttachmentsAsync(dialog.FileNames);
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantViewModel { CanEditDraft: true } model) return;
        try
        {
            if (Clipboard.GetImage() is { } image) Attach(model, image);
            else model.Status = "剪贴板中没有截图。";
        }
        catch (ExternalException) { model.Status = "暂时无法读取剪贴板，请重试。"; }
    }

    private void OnCrop(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantViewModel { CanEditDraft: true, Screenshot: { } bytes } model) return;
        var image = (BitmapSource?)new ScreenshotPreviewConverter().Convert(bytes, typeof(BitmapSource),
            null!, System.Globalization.CultureInfo.InvariantCulture);
        if (image is null) return;
        SelectScreenshot(image);
    }

    public void SelectScreenshot(BitmapSource image, Window? ownerOverride = null)
    {
        if (DataContext is not AiAssistantViewModel { CanEditDraft: true } model || crop is not null) return;
        var owner = ownerOverride ?? Window.GetWindow(this);
        var dialog = new ScreenshotCropWindow(image) { Owner = owner, Topmost = owner?.Topmost == true };
        crop = dialog;
        try
        {
            if (dialog.ShowDialog() == true && dialog.Selection is { } selection) Attach(model, selection);
        }
        finally { crop = null; }
    }

    private void OnPromptKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && DataContext is AiAssistantViewModel model)
        {
            e.Handled = true;
            if (model.AskCommand.CanExecute(null)) model.AskCommand.Execute(null);
        }
    }

    private static void Attach(AiAssistantViewModel model, BitmapSource image)
    {
        var bytes = AiScreenshotCapture.Encode(image);
        if (bytes is null) model.Status = "截图过大，请裁剪后再试。";
        else model.AttachScreenshot(bytes);
    }
}
