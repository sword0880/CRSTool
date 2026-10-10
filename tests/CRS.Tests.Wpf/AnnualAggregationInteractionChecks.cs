using CRS.Application;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class AnnualAggregationInteractionChecks
{
    /// <summary>勾选跨页保留、归属重新确认、成功导航及取消均通过真实页面命令验证。</summary>
    public static async Task RunAsync()
    {
        var cases = new Cases(); var shell = new ShellViewModel(cases,new Interaction()); string? navigated = null;
        shell.NavigateRequested += page=>navigated=page;
        await shell.History.RefreshCommand.ExecuteAsync(null);
        shell.History.SelectableRows[0].IsIncluded = true;
        await shell.History.NextPageCommand.ExecuteAsync(null);
        shell.History.SelectableRows[0].IsIncluded = true;
        if (shell.History.AggregationSelection.Count != 2 || shell.History.AggregateAnnualCommand.CanExecute(null))
            throw new Exception("跨页选择丢失，或未确认归属也允许汇总。");
        shell.History.OwnershipConfirmed = true;
        if (!shell.History.AggregateAnnualCommand.CanExecute(null)) throw new Exception("有效年度范围无法汇总。");
        await shell.History.PreviousPageCommand.ExecuteAsync(null);
        if (!shell.History.SelectableRows[0].IsIncluded || !shell.History.OwnershipConfirmed)
            throw new Exception("翻页未恢复勾选或无故丢失归属确认。");
        shell.History.SelectableRows[1].IsIncluded = true;
        if (shell.History.OwnershipConfirmed) throw new Exception("增加来源后沿用旧归属确认。");
        shell.History.RemoveAggregationTaskCommand.Execute(shell.History.AggregationSelection.Last());
        shell.History.OwnershipConfirmed = true;
        await shell.History.AggregateAnnualCommand.ExecuteAsync(null);
        if (!cases.Ids!.SequenceEqual(new[]{"H000","H050"}) || shell.History.AggregationSelection.Count != 0
            || shell.State.Result?.IsAnnualAggregate != true || navigated != "tax"
            || shell.History.Selected?.Id != "ANNUAL" || shell.Reports.ExportCarryCommand.CanExecute(null))
            throw new Exception("汇总范围、成功后清理／导航／选中或结转门槛错误。");
        var annualRow = shell.History.SelectableRows.Single(r=>r.Task.Id=="ANNUAL"); annualRow.IsIncluded = true;
        if (annualRow.IsIncluded || shell.History.AggregationSelection.Count != 0)
            throw new Exception("年度汇总结果被再次纳入来源。");
        shell.History.SelectableRows[1].IsIncluded = true; shell.History.SelectableRows[2].IsIncluded = true;
        shell.History.OwnershipConfirmed = true; cases.WaitForCancellation = true;
        var pending = shell.History.AggregateAnnualCommand.ExecuteAsync(null); await cases.Started.Task;
        if (shell.History.AggregateAnnualCommand.CanExecute(null) || !shell.History.CancelOperationCommand.CanExecute(null))
            throw new Exception("汇总期间未阻止重复操作或无法取消。");
        shell.History.CancelOperationCommand.Execute(null); await pending;
        if (cases.Saves != 1 || shell.History.AggregationSelection.Count != 2 || shell.State.IsBusy)
            throw new Exception("取消汇总仍保存、丢失选择或遗留忙碌。");
        shell.History.ClearAggregationSelectionCommand.Execute(null);
        if (shell.History.OwnershipConfirmed || shell.History.SelectableRows.Any(r=>r.IsIncluded))
            throw new Exception("清空选择没有同步勾选状态与归属确认。");
        shell.History.SelectableRows.Single(r=>r.Task.Id=="H000").IsIncluded=true;
        shell.History.SelectableRows.Single(r=>r.Task.Id=="H010").IsIncluded=true;
        shell.History.OwnershipConfirmed=true;
        if (shell.History.AggregateAnnualCommand.CanExecute(null) || !shell.History.AggregationHint.Contains("年度不一致"))
            throw new Exception("混合年度仍允许汇总或没有提示。");
        Console.WriteLine("年度汇总交互验证通过：跨页勾选与确认、提交后导航及清理、汇总任务排除、忙碌／取消及保留选择（4 项）。");
    }
    private sealed class Cases : FakeUseCases
    {
        public string[]? Ids { get; private set; }
        public int Saves { get; private set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<HistoryPageResult> QueryHistoryAsync(int pageNumber,int pageSize)
        {
            var rows = Enumerable.Range(0,51).Select(i=>new HistoryItem($"H{i:D3}",i==10?2024:2025,"synthetic",false,0,i==50?"FUTU":"IBKR")).ToList();
            if (Saves>0) rows.Insert(0,new("ANNUAL",2025,"synthetic",false,0,"MULTI",IsAnnualAggregate:true));
            return Task.FromResult(new HistoryPageResult(rows.Skip((pageNumber-1)*pageSize).Take(pageSize).ToList(),rows.Count,pageNumber,pageSize));
        }
        public override async Task<CalculationResult> AggregateAnnualAsync(string[] ids,bool confirmed,CancellationToken cancellation)
        {
            Ids=ids;
            if (!confirmed) throw new Exception("未提交归属确认。");
            if (WaitForCancellation) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite,cancellation); }
            cancellation.ThrowIfCancellationRequested(); Saves++;
            return new() { Year=2025,Summary=new(0,0,0,0,0,0,0),SnapshotId="ANNUAL",Broker="MULTI",
                AggregationSources=[new(ids[0],"IBKR",2025,["A"]),new(ids[1],"FUTU",2025,["B"])] };
        }
    }
    private sealed class Interaction : IUserInteraction
    {
        public string[] PickFiles(string filter,bool multiple)=>[];
        public string? SaveFile(string filter,string name)=>null;
        public void ShowError(string message)=>throw new Exception(message);
        public void OpenFolder(string path) { }
    }
}
