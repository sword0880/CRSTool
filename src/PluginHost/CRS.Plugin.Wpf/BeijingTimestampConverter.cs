using System.Globalization;
using System.Windows.Data;
using CRS.Timing;
namespace CRS.Plugin.Wpf;

/// <summary>只负责WPF绑定适配，普通格式化复用Common；未记录时区的来源仍明确标识。</summary>
public sealed class BeijingTimestampConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
        =>BeijingTime.Display(value,parameter as string??"yyyy-MM-dd HH:mm:ss");
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)
        =>values.Length>1&&values[1] is false&&values[0] is DateTimeOffset original
            ?original.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)+"（原始时区未记录）"
            :Convert(values[0],targetType,parameter,culture);
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture)
        =>targetTypes.Select(_=>Binding.DoNothing).ToArray();
}
