using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CRS.Worker.Ipc;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal abstract class WatchEditor : Window
{
    protected StackPanel Fields { get; } = new();
    protected TextBlock ErrorText { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 8) };
    private bool working;

    protected WatchEditor(Window? owner, string title, double width)
    {
        if (owner is not null) Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * .88;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BrandCanvasBrush");
        Fields.Margin = new(18);
        ErrorText.SetResourceReference(TextBlock.ForegroundProperty, "BrandStatusErrorBrush");
        Content = new ScrollViewer { Content = Fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closing += (_, e) => { if (working) e.Cancel = true; };
    }

    protected void Field(string name, Control control)
    {
        var label = new Label { Content = name, Padding = new(0), Target = control, Margin = new(0, 7, 0, 3) };
        System.Windows.Automation.AutomationProperties.SetName(control, name);
        Fields.Children.Add(label);
        Fields.Children.Add(control);
    }

    protected void Finish(params Button[] buttons)
    {
        Fields.Children.Add(ErrorText);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons) actions.Children.Add(button);
        Fields.Children.Add(actions);
    }

    protected static Button Action(string caption, Action handler)
    {
        var button = new Button { Content = caption, Margin = new(0, 0, 8, 5), Padding = new(9, 5, 9, 5) };
        button.Click += (_, _) => handler();
        return button;
    }

    protected static Button AsyncAction(string caption, Func<Task> handler)
    {
        var button = new Button { Content = caption, Margin = new(0, 0, 8, 5), Padding = new(9, 5, 9, 5) };
        button.Click += async (_, _) => await handler();
        return button;
    }

    protected async Task WithFeedbackAsync(Func<Task> action, CancellationToken lifetime)
    {
        if (working) return;
        working = true;
        var layout = Content as Panel ?? Fields;
        layout.IsEnabled = false;
        ErrorText.Text = "";
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or JsonException or BackgroundServiceException)
        {
            ErrorText.Text = error switch
            {
                ArgumentException invalid => invalid.Message,
                WatchAccessException { Code: "Forbidden" } => "请以 Windows 管理员权限打开 CRS。",
                WatchAccessException access => $"后台未能完成操作（{access.Code}）。",
                BackgroundServiceException communication => communication.Message,
                _ => "操作失败，请检查资料格式和配套后台服务。"
            };
        }
        finally { layout.IsEnabled = true; working = false; }
    }

    protected void CloseAfterSave()
    {
        working = false;
        DialogResult = true;
    }
}
