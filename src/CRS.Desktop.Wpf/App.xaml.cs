using System.Windows;
using CRS.DesktopClient.Composition;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;

namespace CRS.DesktopClient;

public partial class App : System.Windows.Application
{
    /// <summary>组合根连接后台和前台，界面不直接构造解析器或数据库。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--smoke-test"))
        {
            DispatcherUnhandledException += (_, failure) =>
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CRS-UI-smoke-error.txt"), failure.Exception.ToString());
                failure.Handled = true; Shutdown(1);
            };
        }
        try
        {
            var smoke = e.Args.Contains("--smoke-test");
            if (smoke) Console.Error.WriteLine("smoke: isolated store");
            var runtime = DesktopComposition.Create(smoke);
            var viewModel = new ShellViewModel(runtime.UseCases, smoke ? new SmokeInteraction() : new UserInteraction(), runtime.Settings);
            if (smoke) Console.Error.WriteLine("smoke: create navigation shell");
            var window = new MainWindow(viewModel); MainWindow = window;
            if (smoke)
            {
                window.Loaded += async (_, _) =>
                {
                    try { await SmokeCheck.RunAsync(window, viewModel, runtime.Operations, e.Args); Shutdown(0); }
                    catch (Exception error) { System.IO.File.WriteAllText(System.IO.Path.Combine(runtime.DirectoryPath, "smoke-error.txt"), error.ToString()); Shutdown(1); }
                };
            }
            window.Show();
            if (smoke) Console.Error.WriteLine("smoke: window shown");
            if (!smoke) _ = viewModel.History.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception error)
        {
            if (e.Args.Contains("--smoke-test")) System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CRS-UI-smoke-error.txt"), error.ToString());
            DesktopComposition.LogStartupFailure(error);
            if (!e.Args.Contains("--smoke-test")) MessageBox.Show("程序无法启动，请检查本地数据目录及配置文件。", "海外证券个税助手");
            Shutdown(1);
        }
    }
    /// <summary>退出时清空日志资源，令牌随窗口会话释放。</summary>
    protected override void OnExit(ExitEventArgs e) { DesktopComposition.Shutdown(); base.OnExit(e); }
}
