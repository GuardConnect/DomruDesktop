using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace Domru.Desktop.Controls;
// Persistent native child window: never creates a separate VLC output window.
public sealed class VideoSurface : HwndHost
{
    private IntPtr handle;
    private readonly List<IntPtr> hooked=[];
    private static readonly SubclassProc callback=DispatchMessage;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr,WeakReference<VideoSurface>> owners=new();
    private long lastClick;
    private int regionWidth,regionHeight;
    public static readonly DependencyProperty CornerRadiusProperty=DependencyProperty.Register(nameof(CornerRadius),typeof(double),typeof(VideoSurface),new PropertyMetadata(18d,OnCornerRadiusChanged));
    public double CornerRadius{get=>(double)GetValue(CornerRadiusProperty);set=>SetValue(CornerRadiusProperty,value);}
    private static void OnCornerRadiusChanged(DependencyObject target,DependencyPropertyChangedEventArgs args){var surface=(VideoSurface)target;surface.regionWidth=surface.regionHeight=0;surface.UpdateRegion();}
    public event Action? DoubleClick;
    public event Action? Escape;
    public new IntPtr Handle=>handle;
    protected override void OnWindowPositionChanged(Rect rectangle)
    {
        base.OnWindowPositionChanged(rectangle);
        UpdateRegion();
    }
    private void UpdateRegion()
    {
        if(handle==IntPtr.Zero||!GetClientRect(handle,out var size))return;
        var width=size.Right-size.Left;var height=size.Bottom-size.Top;
        if(width<=0||height<=0||width==regionWidth&&height==regionHeight)return;
        regionWidth=width;regionHeight=height;var radius=(int)Math.Round(Math.Max(0,CornerRadius)*System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX);
        if(radius==0){SetWindowRgn(handle,IntPtr.Zero,true);return;}
        var region=CreateRoundRectRgn(0,0,width+1,height+1,radius*2,radius*2);
        if(region!=IntPtr.Zero&&SetWindowRgn(handle,region,true)==0)DeleteObject(region);
    }
    internal bool HasRoundedRegion
    {
        get{if(handle==IntPtr.Zero)return false;var region=CreateRoundRectRgn(0,0,1,1,0,0);if(region==IntPtr.Zero)return false;try{return GetWindowRgn(handle,region)==3;}finally{DeleteObject(region);}}
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        handle=CreateWindowEx(0,"STATIC","",0x40000000|0x10000000|0x02000000|0x100,0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(handle==IntPtr.Zero)throw new InvalidOperationException("Не удалось создать видеополе");Hook(handle);return new(this,handle);
    }
    public void HookVideoWindows(){if(handle==IntPtr.Zero)return;EnumChildWindows(handle,(child,_)=>{Hook(child);return true;},IntPtr.Zero);}
    private void Hook(IntPtr child){if(hooked.Contains(child)&&IsWindow(child))return;if(SetWindowSubclass(child,callback,1,UIntPtr.Zero)){hooked.Add(child);owners[child]=new(this);}}
    private static IntPtr DispatchMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data)
    {
        var result=owners.TryGetValue(window,out var weak)&&weak.TryGetTarget(out var owner)?owner.WindowProcedure(window,message,wParam,lParam,id,data):DefSubclassProc(window,message,wParam,lParam);
        if(message==0x0082)owners.TryRemove(window,out _);return result;
    }
    private IntPtr WindowProcedure(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data)
    {
        if(message==0x0201 || (message==0x0210&&(wParam.ToInt64()&0xffff)==0x0201)){var now=Environment.TickCount64;if(now-lastClick<15)return DefSubclassProc(window,message,wParam,lParam);if(lastClick!=0&&now-lastClick<=GetDoubleClickTime()){lastClick=0;Dispatcher.BeginInvoke(()=>DoubleClick?.Invoke());}else lastClick=now;}
        if(message==0x0203){lastClick=0;Dispatcher.BeginInvoke(()=>DoubleClick?.Invoke());}
        if(message==0x0100&&wParam.ToInt32()==27)Dispatcher.BeginInvoke(()=>Escape?.Invoke());
        return DefSubclassProc(window,message,wParam,lParam);
    }
    protected override void DestroyWindowCore(HandleRef window){foreach(var child in hooked){if(IsWindow(child))RemoveWindowSubclass(child,callback,1);owners.TryRemove(child,out _);}hooked.Clear();DestroyWindow(window.Handle);handle=IntPtr.Zero;regionWidth=regionHeight=0;}
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate IntPtr SubclassProc(IntPtr h,uint msg,IntPtr w,IntPtr l,UIntPtr id,UIntPtr data);
    private delegate bool EnumProc(IntPtr h,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr CreateWindowEx(int ex,string cls,string name,int style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr window,out NativeRect rectangle);
    [DllImport("user32.dll")]private static extern int SetWindowRgn(IntPtr window,IntPtr region,bool redraw);
    [DllImport("user32.dll")]private static extern int GetWindowRgn(IntPtr window,IntPtr region);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int ellipseWidth,int ellipseHeight);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr region);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr data);
    [DllImport("user32.dll")]private static extern uint GetDoubleClickTime();
    [DllImport("comctl32.dll")]private static extern bool SetWindowSubclass(IntPtr h,SubclassProc callback,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")]private static extern bool RemoveWindowSubclass(IntPtr h,SubclassProc callback,UIntPtr id);
    [DllImport("comctl32.dll")]private static extern IntPtr DefSubclassProc(IntPtr h,uint msg,IntPtr w,IntPtr l);
}
