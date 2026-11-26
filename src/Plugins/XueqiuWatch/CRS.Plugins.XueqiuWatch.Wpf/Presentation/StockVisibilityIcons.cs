using System.Windows.Media;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal static class StockVisibilityIcons
{
    internal static readonly Geometry Visible = Geometry.Parse(
        "M2 12 C7 5 17 5 22 12 C17 19 7 19 2 12 Z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12");
    internal static readonly Geometry Hidden = Geometry.Parse(
        "M2 12 C3.4 10 5 8.6 7 7.8 M11 7 C15.4 6.6 19.4 8.4 22 12 C21.2 13.2 20.2 14.2 19.1 15 " +
        "M15.6 16.7 C10.6 18.6 5.5 16.9 2 12 M3 3 L21 21");

    static StockVisibilityIcons()
    {
        Visible.Freeze();
        Hidden.Freeze();
    }
}
