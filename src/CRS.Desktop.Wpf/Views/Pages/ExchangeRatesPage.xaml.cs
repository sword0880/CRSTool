using System.Windows.Controls;
using CRS.DesktopClient.ViewModels;
namespace CRS.DesktopClient.Views.Pages;
/// <summary>绑定ExchangeRatesPage页面模型，业务行为由应用用例负责。</summary>
public partial class ExchangeRatesPage : Page
{
    public ExchangeRatesPage(ShellViewModel shell) { InitializeComponent(); DataContext = shell.ExchangeRates; }
    private async void OnReadRates(object sender, System.Windows.RoutedEventArgs e)
    {
        var model = (ExchangeRatesViewModel)DataContext;
        if (!model.State.IsIdle) return;
        // 直接校验当前文本，确保按钮不获取焦点时也读取用户刚输入的年度。
        if (!int.TryParse(YearInput.Text, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var year) || year is < 2000 or > 2100)
        {
            model.RateStatus = model.State.Status = "请输入 2000 至 2100 之间的有效年度。";
            YearInput.Focus();
            return;
        }
        // 同步控件数值与绑定模型，再执行后台读取；保留原有数值绑定。
        YearInput.SetCurrentValue(Wpf.Ui.Controls.NumberBox.ValueProperty, (double)year);
        model.Year = year;
        if (model.RefreshCommand.CanExecute(null)) await model.RefreshCommand.ExecuteAsync(null);
    }
}
