using CRS.Application;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class ImportInteractionChecks
{
    public static async Task RunAsync()
    {
        var cases = new FakeUseCases(); var picker = new TestPicker(); var shell = new ShellViewModel(cases, picker);
        shell.Import.Broker = "FUTU";
        picker.Files = ["wrong.pdf"]; shell.Import.PickFutuDividendCommand.Execute(null);
        picker.Files = ["trades.xlsx"]; shell.Import.PickFutuTradesCommand.Execute(null);
        await shell.State.DisplayAsync(cases.Result);
        picker.Files = ["dividends.xlsx"]; shell.Import.PickFutuDividendCommand.Execute(null);
        if (shell.State.HasResult || shell.Import.FutuTradesLabel != "trades.xlsx") throw new Exception("替换股息文件未清除结果或影响交易文件。");
        picker.Files = []; shell.Import.PickFutuTradesCommand.Execute(null);
        if (shell.Import.FutuTradesLabel != "trades.xlsx") throw new Exception("取消选择清除了现有文件。");
        await shell.Import.CalculateCommand.ExecuteAsync(null);
        if (cases.LastRequest is null || !cases.LastRequest.Files.SequenceEqual(new[] { "dividends.xlsx", "trades.xlsx" })) throw new Exception("分开的富途文件未提交正确集合。");
        shell.Import.ClearFutuDividendCommand.Execute(null);
        if (shell.State.HasResult || shell.Import.FutuTradesLabel != "trades.xlsx" || shell.Import.ClearFutuDividendCommand.CanExecute(null)) throw new Exception("清除收入文件影响了交易或导出状态。");
        shell.Import.ClearFutuTradesCommand.Execute(null);
        if (shell.Import.CalculateCommand.CanExecute(null)) throw new Exception("没有文件仍允许计算。");
        picker.Files = ["retry.pdf"]; shell.Import.PickFutuDividendCommand.Execute(null);
        shell.State.IsBusy = true;
        if (shell.Import.PickFutuDividendCommand.CanExecute(null) || shell.Import.ClearFutuDividendCommand.CanExecute(null)) throw new Exception("运行中仍允许修改富途输入。");
        shell.State.IsBusy = false; shell.Import.Broker = "IBKR"; shell.Import.Broker = "FUTU";
        if (shell.Import.CalculateCommand.CanExecute(null)) throw new Exception("切换券商保留了旧文件。");
        // 期间清单独立选择；取消保持，清除与切换券商不能沿用旧声明。
        picker.Files=["periods.json"]; shell.Import.PickAccountPeriodsCommand.Execute(null);
        picker.Files=[]; shell.Import.PickAccountPeriodsCommand.Execute(null);
        if(shell.Import.AccountPeriodsLabel!="periods.json") throw new Exception("取消选择清除了期间清单。");
        picker.Files=["income.xlsx"]; shell.Import.PickFutuDividendCommand.Execute(null);
        await shell.Import.CalculateCommand.ExecuteAsync(null);
        if(cases.LastRequest?.AccountPeriodsPath!="periods.json") throw new Exception("期间清单未传入计算请求。");
        shell.State.IsBusy=true;
        if(shell.Import.PickAccountPeriodsCommand.CanExecute(null) || shell.Import.ClearAccountPeriodsCommand.CanExecute(null))
            throw new Exception("忙碌时仍允许改变期间依据。");
        shell.State.IsBusy=false; shell.Import.ClearAccountPeriodsCommand.Execute(null);
        await shell.Import.CalculateCommand.ExecuteAsync(null);
        if(cases.LastRequest?.AccountPeriodsPath is not null) throw new Exception("清除后仍提交期间依据。");
        picker.Files=["periods.json"]; shell.Import.PickAccountPeriodsCommand.Execute(null); shell.Import.Broker="IBKR";
        if(shell.Import.AccountPeriodsLabel=="periods.json") throw new Exception("切换券商保留了期间依据。");
        Console.WriteLine("期间导入交互通过：选择／取消、请求传递、忙碌、清除及券商切换隔离。");
        Console.WriteLine("富途交互验证通过：独立替换、取消保持、提交与清除、忙碌与券商切换（4 项）。");
    }
    private sealed class TestPicker : IUserInteraction
    {
        public string[] Files { get; set; } = [];
        public string[] PickFiles(string filter, bool multiple) => Files;
        public string? SaveFile(string filter, string fileName) => null;
        public void ShowError(string message) => throw new Exception(message);
        public void OpenFolder(string path) { }
    }
}
