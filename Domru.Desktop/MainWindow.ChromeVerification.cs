using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Domru.Desktop;
public partial class MainWindow
{
    private async Task VerifyWindowChromeAsync()
    {
        int Hit(double x,double y)
        {
            var point=PointToScreen(new Point(x,y));
            var packed=unchecked((int)(((uint)(ushort)(short)point.Y<<16)|(ushort)(short)point.X));
            return SendMessage(new WindowInteropHelper(this).Handle,0x0084,IntPtr.Zero,new IntPtr(packed)).ToInt32();
        }
        void VerifyHeader()
        {
            if(Hit(400,15)!=2||Hit(400,55)!=2)throw new InvalidOperationException("Верхняя область не распознана как системный заголовок");
            if(Hit(ActualWidth-30,15)!=1)throw new InvalidOperationException("Кнопка закрытия не доступна для клика");
        }
        try
        {
            await Task.Delay(300);VerifyHeader();
            WindowState=WindowState.Maximized;await Task.Delay(300);VerifyHeader();
            WindowState=WindowState.Normal;await Task.Delay(200);VerifyHeader();
            HomePanel.Visibility=VideoPanel.Visibility=Visibility.Visible;LoginPanel.Visibility=Visibility.Collapsed;
            ToggleFullscreen();await Task.Delay(200);
            if(WindowChrome.GetWindowChrome(this).CaptionHeight!=0)throw new InvalidOperationException("Полноэкранное видео сохраняет заголовок");
            ExitFullscreen();await Task.Delay(200);VerifyHeader();
            var root=Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR")??Path.Combine(Environment.CurrentDirectory,"artifacts","ui-0.1.28");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root,"window-chrome.txt"),"PASS: normal/maximized/restored header HTCAPTION; close button HTCLIENT; fullscreen caption disabled and restored.");
        }
        finally{closing=true;Close();}
    }
}
