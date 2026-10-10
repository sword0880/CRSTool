using CRS.Application;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class ForeignCreditInteractionChecks
{
    /// <summary>验证取消文件选择、忙碌门槛及复核成功后刷新与选中派生任务。</summary>
    public static async Task RunAsync()
    {
        var cases=new Cases(); var interaction=new Interaction(); var shell=new ShellViewModel(cases,interaction);
        await shell.History.RefreshCommand.ExecuteAsync(null); shell.History.Selected=shell.History.Rows[0];
        await shell.History.ReviewForeignCreditCommand.ExecuteAsync(null);
        if (cases.Called!=0) throw new Exception("取消选择仍提交抵免复核。");
        shell.State.IsBusy=true;
        if (shell.History.ReviewForeignCreditCommand.CanExecute(null)) throw new Exception("忙碌时允许抵免复核。");
        shell.State.IsBusy=false; interaction.Path="synthetic.json";
        await shell.History.ReviewForeignCreditCommand.ExecuteAsync(null);
        if (cases.Called!=1 || shell.State.Result?.SnapshotId!="CREDIT-CHILD" || shell.History.Selected?.Id!="CREDIT-CHILD"
            || shell.State.Fingerprint!="") throw new Exception("抵免复核后未显示、刷新或沿用冻结汇率。");
        Console.WriteLine("抵免交互验证通过：取消选择不调用、忙碌门槛、另存后展示和选中（3 项）。");
    }
    private sealed class Cases : FakeUseCases
    {
        public int Called {get;private set;}
        public override Task<CalculationResult> ReviewForeignCreditAsync(string id,string path,CancellationToken cancellation)
        {
            if (id!="SYNTHETIC" || path!="synthetic.json") throw new Exception("抵免复核提交的任务或文件不正确。");
            Called++; return Task.FromResult(new CalculationResult { Year=2025,SnapshotId="CREDIT-CHILD",ParentSnapshotId=id,Summary=new(0,0,0,0,0,0,0) });
        }
        public override async Task<HistoryPageResult> QueryHistoryAsync(int pageNumber,int pageSize)
        {
            var rows=await HistoryAsync(); if (Called>0) rows.Insert(0,new("CREDIT-CHILD",2025,"synthetic",false,1,ParentSnapshotId:"SYNTHETIC"));
            return new(rows,rows.Count,1,pageSize);
        }
    }
    private sealed class Interaction : IUserInteraction
    {
        public string? Path {get;set;}
        public string[] PickFiles(string filter,bool multiple)=>Path is null?[]:[Path];
        public string? SaveFile(string filter,string name)=>null;
        public void ShowError(string message)=>throw new Exception(message);
        public void OpenFolder(string path){}
    }
}
