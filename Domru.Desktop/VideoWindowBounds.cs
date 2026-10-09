using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace Domru.Desktop;
internal static class VideoWindowBounds
{
    public static Rect Get(Window window)
    {
        var monitor=MonitorFromWindow(new WindowInteropHelper(window).Handle,2);var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfo(monitor,ref info))return new Rect(0,0,SystemParameters.PrimaryScreenWidth,SystemParameters.PrimaryScreenHeight);
        var transform=PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice??System.Windows.Media.Matrix.Identity;
        var topLeft=transform.Transform(new Point(info.Monitor.Left,info.Monitor.Top));var bottomRight=transform.Transform(new Point(info.Monitor.Right,info.Monitor.Bottom));return new Rect(topLeft,bottomRight);
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo{public int Size;public NativeRect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
}
