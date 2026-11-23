using System.Windows;
using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views;

public partial class PluginPlanPickerWindow : FluentWindow
{
    internal PluginPlanPickerWindow(IReadOnlyList<PluginPlanOption> options, CancellationToken lifetime)
    {
        InitializeComponent();
        PluginTasks.SelectionChanged += OnSelectionChanged;
        PluginTasks.ItemsSource = options;
        if (options.Count == 0) PickerHint.Text = "当前后台没有提供可定时执行的插件任务。";
        else if (options.All(option => option.Plan is null or { Revision: > 0 }))
            PickerHint.Text = options.Any(option => option.Plan is null)
                ? "已添加的任务可打开编辑。选中暂不可添加的任务可查看原因。"
                : "当前插件任务均已添加，可选择一项查看或修改计划。";
        PluginTasks.SelectedItem = options.FirstOrDefault(option => option.Plan is { Revision: 0 })
            ?? options.FirstOrDefault(option => option.Detail is not null);
        var registration = lifetime.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) Close(); })));
        Closed += (_, _) => registration.Dispose();
    }

    public TaskDefinition? SelectedPlan => (PluginTasks.SelectedItem as PluginPlanOption)?.Plan;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContinueButton.IsEnabled = SelectedPlan is not null;
        ContinueButton.Content = SelectedPlan is { Revision: > 0 } ? "编辑计划" : "设置计划";
        SelectionDetail.Text = (PluginTasks.SelectedItem as PluginPlanOption)?.Detail ?? "";
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (SelectedPlan is not null) DialogResult = true;
    }
}
