using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;
namespace Domru.Desktop;
public partial class MainWindow
{
    private DispatcherTimer? trayHoverTimer;
    private long trayOutsideSince;
    private Drawing.Point? lastTrayMousePoint;
    private void InitializeTrayHover()
    {
        trayIcon!.MouseMove+=(_,e)=>lastTrayMousePoint=new Drawing.Point(e.X,e.Y);
        trayHoverTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(75)};
        trayHoverTimer.Tick+=(_,_)=>CheckTrayHover(Forms.Cursor.Position,Environment.TickCount64);
        trayMenu!.Opened+=(_,_)=>{trayOutsideSince=0;trayHoverTimer.Start();};
        trayMenu.Closed+=(_,_)=>{trayHoverTimer?.Stop();trayOutsideSince=0;};
    }
    private void CheckTrayHover(Drawing.Point cursor,long now)
    {
        if(trayMenu?.Visible!=true){trayHoverTimer?.Stop();return;}
        if(trayMenu.Bounds.Contains(cursor)||IsOverTrayIcon(cursor)){trayOutsideSince=0;return;}
        if(trayOutsideSince==0)trayOutsideSince=now;
        else if(now-trayOutsideSince>=400)trayMenu.Close(Forms.ToolStripDropDownCloseReason.AppClicked);
    }
    private bool IsOverTrayIcon(Drawing.Point cursor)
    {
        if(TryGetTrayIconBounds(out var bounds))return bounds.Contains(cursor);
        return lastTrayMousePoint is {} point&&point==cursor;
    }
    private bool TryGetTrayIconBounds(out Drawing.Rectangle bounds)
    {
        bounds=Drawing.Rectangle.Empty;if(trayIcon is null)return false;
        // NotifyIcon has no public bounds API. The bundled .NET 10 fields identify our own shell icon.
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(Forms.NotifyIcon);
        if(type.GetField("_window",flags)?.GetValue(trayIcon) is not Forms.NativeWindow window||type.GetField("_id",flags)?.GetValue(trayIcon) is not uint id)return false;
        var identifier=new IconIdentifier{Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=window.Handle,Id=id};
        if(Shell_NotifyIconGetRect(ref identifier,out var rect)!=0)return false;
        bounds=Drawing.Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);return !bounds.IsEmpty;
    }
    [StructLayout(LayoutKind.Sequential)]private struct IconIdentifier{public uint Size;public IntPtr Window;public uint Id;public Guid Guid;}
    [StructLayout(LayoutKind.Sequential)]private struct IconRectangle{public int Left,Top,Right,Bottom;}
    [DllImport("shell32.dll")]private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier,out IconRectangle rect);
    private async Task VerifyTrayHoverAsync()
    {
        var point=Forms.Cursor.Position;var working=Forms.Screen.PrimaryScreen!.WorkingArea;
        var location=new Drawing.Point(point.X<working.Left+working.Width/2?working.Right-300:working.Left+30,point.Y<working.Top+working.Height/2?working.Bottom-260:working.Top+30);
        trayMenu!.Show(location);await Task.Delay(75);
        var inside=new Drawing.Point(trayMenu.Bounds.Left+5,trayMenu.Bounds.Top+5);CheckTrayHover(inside,1000);if(!trayMenu.Visible)throw new InvalidOperationException("Меню закрылось при наведении");
        if(TryGetTrayIconBounds(out var iconBounds)){CheckTrayHover(new Drawing.Point(iconBounds.Left+iconBounds.Width/2,iconBounds.Top+iconBounds.Height/2),1500);if(!trayMenu.Visible)throw new InvalidOperationException("Меню закрылось над значком трея");}
        var outside=new Drawing.Point(-32000,-32000);CheckTrayHover(outside,2000);CheckTrayHover(outside,2200);if(!trayMenu.Visible)throw new InvalidOperationException("Нет паузы перехода к меню");
        CheckTrayHover(outside,2500);if(trayMenu.Visible)throw new InvalidOperationException("Меню не скрывается вне значка и меню");
    }
}
