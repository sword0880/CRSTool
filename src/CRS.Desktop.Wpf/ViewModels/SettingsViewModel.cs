using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;

namespace CRS.DesktopClient.ViewModels;

/// <summary>设置窗口只收集选项，持久化和路径校验交给后台。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IDesktopSettingsService service;
    public WorkspaceState State { get; }
    [ObservableProperty] private string databaseDirectory = "";
    [ObservableProperty] private string logDirectory = "";
    [ObservableProperty] private bool loggingEnabled;
    [ObservableProperty] private string logLevel = "Info";
    [ObservableProperty] private int logRetentionFiles;
    [ObservableProperty] private int logFileSizeMb;
    [ObservableProperty] private string message = "保存后下次启动生效；更换数据库目录不会移动原数据库。";
    public string[] LogLevels { get; } = ["Debug", "Info", "Warn", "Error"];

    public SettingsViewModel(WorkspaceState state, IDesktopSettingsService service)
    {
        State = state; this.service = service; Reload();
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) SaveCommand.NotifyCanExecuteChanged(); };
    }
    public void Reload()
    {
        var settings = service.Load();
        DatabaseDirectory = settings.DatabaseDirectory; LogDirectory = settings.LogDirectory;
        LoggingEnabled = settings.LoggingEnabled; LogLevel = settings.LogLevel;
        LogRetentionFiles = settings.LogRetentionFiles; LogFileSizeMb = settings.LogFileSizeMb;
        Message = "保存后下次启动生效；更换数据库目录不会移动原数据库。";
    }
    private bool CanSave() => State.IsIdle;
    [RelayCommand]
    private void BrowseDatabase() { var path = State.Interaction.PickFolder(); if (path is not null) DatabaseDirectory = path; }
    [RelayCommand]
    private void BrowseLogs() { var path = State.Interaction.PickFolder(); if (path is not null) LogDirectory = path; }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var settings = new DesktopSettings(DatabaseDirectory, LogDirectory, LoggingEnabled, LogLevel, LogRetentionFiles, LogFileSizeMb);
        if (await State.RunAsync(() => service.SaveAsync(settings)))
        { Message = "设置已保存，下次启动生效。当前任务和数据库保持不变。"; State.Status = Message; }
    }
}
