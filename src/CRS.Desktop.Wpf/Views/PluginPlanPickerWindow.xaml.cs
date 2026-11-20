using System.Windows;
using System.Windows.Controls;
using CRS.Worker.Abstractions;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views;

public partial class PluginPlanPickerWindow : FluentWindow
{
    public PluginPlanPickerWindow(IReadOnlyList<TaskDefinition> options, CancellationToken lifetime)
    {
        InitializeComponent();
        PluginTasks.SelectionChanged += OnSelectionChanged;
        PluginTasks.ItemsSource = options;
        if (options.Count == 0) PickerHint.Text = "当前后台没有提供可定时执行的插件任务。";
        else if (options.All(option => option.Revision != 0)) PickerHint.Text = "当前插件任务均已添加，可选择一项查看或修改计划。";
        PluginTasks.SelectedItem = options.FirstOrDefault(option => option.Revision == 0);
        var registration = lifetime.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) Close(); })));
        Closed += (_, _) => registration.Dispose();
    }

    public TaskDefinition? SelectedPlan => PluginTasks.SelectedItem as TaskDefinition;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContinueButton.IsEnabled = SelectedPlan is not null;
        ContinueButton.Content = SelectedPlan is { Revision: > 0 } ? "编辑计划" : "设置计划";
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (SelectedPlan is not null) DialogResult = true;
    }
}
