using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

internal static class DatePickerStyleChecks
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var bindingTrace = PresentationTraceSources.DataBindingSource;
            var originalLevel = bindingTrace.Switch.Level;
            using var trace = new System.IO.StringWriter();
            using var listener = new TextWriterTraceListener(trace);
            bindingTrace.Switch.Level = SourceLevels.Error;
            bindingTrace.Listeners.Add(listener);
            try
            {
                var app = new Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources.MergedDictionaries.Add(new() { Source = new("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml") });
                app.Resources.MergedDictionaries.Add(new() { Source = new("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml") });

                var picker = new DatePicker { Width = 150, SelectedDateFormat = DatePickerFormat.Short };
                var root = new Border { Child = picker };
                root.Measure(new(200, 100));
                root.Arrange(new Rect(0, 0, 200, 100));
                root.UpdateLayout();
                picker.ApplyTemplate();
                if (picker.Template?.FindName("PART_Button", picker) is not Wpf.Ui.Controls.Button button
                    || button.Content is not Wpf.Ui.Controls.SymbolIcon)
                    throw new InvalidOperationException("日期选择器未使用带日历图标的 WPF UI 模板。");
                button.ApplyTemplate();
                var content = Descendants(button).OfType<ContentPresenter>().Single(presenter => ReferenceEquals(presenter.Content, button.Content));
                button.PressedForeground = Brushes.Goldenrod;

                // 设置只读交互状态，在无鼠标和前台窗口的测试桌面也能执行按下样式。
                SetState(button, typeof(UIElement), UIElement.IsMouseOverProperty, true);
                SetState(button, typeof(ButtonBase), ButtonBase.IsPressedProperty, true);
                picker.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                if (TextElement.GetForeground(content) != button.PressedForeground
                    || trace.ToString().Contains("PressedForeground", StringComparison.Ordinal))
                    throw new InvalidOperationException("日历按钮按下时未从按钮取得正确的前景色。" + Environment.NewLine + trace);

                SetState(button, typeof(ButtonBase), ButtonBase.IsPressedProperty, false);
                SetState(button, typeof(UIElement), UIElement.IsMouseOverProperty, false);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
                if (!picker.IsDropDownOpen || popup.Child is not Calendar calendar)
                    throw new InvalidOperationException("日历按钮未打开日期下拉框。");
                var day = new DateTime(2025, 6, 18);
                calendar.SelectedDate = day;
                if (picker.SelectedDate != day)
                    throw new InvalidOperationException("下拉日历的选择未同步到日期控件。");
                picker.IsDropDownOpen = false;
                Console.WriteLine("日期选择器通过：按下无前景色绑定错误，日历正常打开并提交日期。");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                bindingTrace.Listeners.Remove(listener);
                bindingTrace.Switch.Level = originalLevel;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void SetState(DependencyObject element, Type owner, DependencyProperty property, bool value)
    {
        var key = owner.GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(DependencyPropertyKey))
            .Select(field => (DependencyPropertyKey?)field.GetValue(null))
            .Single(key => key?.DependencyProperty == property);
        element.SetValue(key!, value);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
