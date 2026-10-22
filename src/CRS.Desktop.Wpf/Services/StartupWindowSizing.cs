using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CRS.DesktopClient.Services;

/// <summary>首次显示前按所在显示器的工作区和 DPI 收缩窗口，给任务栏和屏幕边缘留出空间。</summary>
internal static class StartupWindowSizing
{
    internal static void Attach(Window window) => window.SourceInitialized += (_, _) =>
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var scale = GetDpiForWindow(handle) / 96d;
        if (scale <= 0) return;
        var width = Math.Max(1, (info.Work.Right - info.Work.Left) / scale - 32);
        var height = Math.Max(1, (info.Work.Bottom - info.Work.Top) / scale - 32);
        // 小屏幕时也必须降低最小尺寸，否则 WPF 会把窗口重新撑出工作区。
        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        window.Width = Math.Min(window.Width, width);
        window.Height = Math.Min(window.Height, height);
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
