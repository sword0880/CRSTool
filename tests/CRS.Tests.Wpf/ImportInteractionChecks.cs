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
