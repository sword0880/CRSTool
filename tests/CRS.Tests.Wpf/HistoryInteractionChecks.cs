using CRS.Application;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class HistoryInteractionChecks
{
    /// <summary>历史翻页不污染概览；忙碌、取消及复算后的选中项均验证真实命令。</summary>
    public static async Task RunAsync()
    {
        var cases = new Cases(); var shell = new ShellViewModel(cases, new Interaction());
        await shell.History.RefreshCommand.ExecuteAsync(null);
        var recent = shell.Dashboard.RecentTasks.Select(r => r.Id).ToArray();
        shell.History.Selected = shell.History.Rows[0];
        await shell.History.NextPageCommand.ExecuteAsync(null);
        if (shell.History.PageNumber != 2 || shell.History.Selected is not null
            || !recent.SequenceEqual(shell.Dashboard.RecentTasks.Select(r => r.Id)))
            throw new Exception("历史翻页遗留选中项，或把概览最新任务替换为旧页。");
        await shell.History.NextPageCommand.ExecuteAsync(null);
        if (shell.History.Rows.Count != 5 || shell.History.NextPageCommand.CanExecute(null))
            throw new Exception("历史末页数量或下一页门槛错误。");
        shell.State.IsBusy = true;
        if (shell.History.PreviousPageCommand.CanExecute(null) || shell.History.RefreshCommand.CanExecute(null))
            throw new Exception("后台忙碌仍能翻页。");
        shell.State.IsBusy = false;
        await shell.History.RefreshCommand.ExecuteAsync(null); shell.History.Selected = shell.History.Rows[0];
        var replayTask = shell.History.ReplayCommand.ExecuteAsync(null);
        await cases.Started.Task;
        if (!shell.History.CancelOperationCommand.CanExecute(null) || shell.History.OpenCommand.CanExecute(null))
            throw new Exception("复算期间取消或并发门槛错误。");
        shell.History.CancelOperationCommand.Execute(null); await replayTask;
        if (cases.Saved || shell.State.HasResult || shell.State.IsBusy)
            throw new Exception("取消复算仍保存结果，或未解除忙碌。");
        cases.CancelOnly = false;
        await shell.History.ReplayCommand.ExecuteAsync(null);
        if (!cases.Saved || shell.State.Result?.ParentSnapshotId != "H000" || shell.History.Selected?.Id != "CHILD"
            || shell.History.TotalCount != 106)
            throw new Exception("复算成功后未刷新任务、保留父任务或选中新运行。");
        Console.WriteLine("历史交互验证通过：分页与概览隔离、页边界与忙碌、取消、复算后选中（4 项）。");
    }
    private sealed class Cases : FakeUseCases
    {
        public bool CancelOnly { get; set; } = true;
        public bool Saved { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<HistoryPageResult> QueryHistoryAsync(int pageNumber,int pageSize)
        {
            var rows = Enumerable.Range(0,105).Select(i=>new HistoryItem($"H{i:D3}",2025,"synthetic",false,0)).ToList();
            if (Saved) rows.Insert(0,new("CHILD",2025,"synthetic",false,0,ParentSnapshotId:"H000"));
            return Task.FromResult(new HistoryPageResult(rows.Skip((pageNumber-1)*pageSize).Take(pageSize).ToList(),rows.Count,pageNumber,pageSize));
        }
        public override async Task<CalculationResult> ReplayAsync(string id,CancellationToken cancellation)
        {
            Started.TrySetResult();
            if (CancelOnly) await Task.Delay(Timeout.Infinite,cancellation);
            cancellation.ThrowIfCancellationRequested(); Saved = true;
            return new() { Year=2025,Summary=new(0,0,0,0,0,0,0),SnapshotId="CHILD",ParentSnapshotId=id };
        }
    }
    private sealed class Interaction : IUserInteraction
    {
        public string[] PickFiles(string filter,bool multiple)=>[];
        public string? SaveFile(string filter,string fileName)=>null;
        public void ShowError(string message)=>throw new Exception(message);
        public void OpenFolder(string path) { }
    }
}
