using System.ComponentModel;
using System.Windows;
using Domru.Desktop.Core;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;
namespace Domru.Desktop;
public partial class MainWindow
{
    private Forms.NotifyIcon? trayIcon;
    private Drawing.Icon? trayImage;
    private Forms.ContextMenuStrip? trayMenu;
    private bool exitRequested,backgroundSettingsReady;
    private WindowState trayRestoreState=WindowState.Normal;
    private readonly StartupRegistration startupRegistration=new();
    private bool IsDiagnosticRun=>Environment.GetCommandLineArgs().Any(x=>x=="--smoke"||x.StartsWith("--verify-",StringComparison.Ordinal));
    private void InitializeBackgroundFeatures()
    {
        try{StartupBox.IsChecked=startupRegistration.IsEnabled();StartupStatus.Text=StartupBox.IsChecked==true?"При входе в Windows приложение запускается в трее":"Автозагрузка отключена";}catch{StartupBox.IsChecked=false;StartupStatus.Text="Не удалось проверить автозагрузку Windows";}
        backgroundSettingsReady=true;
        if(IsDiagnosticRun&&!Environment.GetCommandLineArgs().Contains("--verify-tray"))return;
        using(var resource=System.Windows.Application.GetResourceStream(new Uri("/Assets/app.ico",UriKind.Relative))!.Stream)
        using(var source=new Drawing.Icon(resource))trayImage=new Drawing.Icon(source,new Drawing.Size(16,16));
        trayMenu=new Forms.ContextMenuStrip{ShowImageMargin=false,BackColor=Drawing.Color.White,ForeColor=Drawing.Color.FromArgb(25,29,39),Font=new Drawing.Font("Segoe UI",9.5f)};
        void Add(string text,Func<Task> action){trayMenu.Items.Add(text,null,(_,_)=>Dispatcher.BeginInvoke(new Action(async()=>{try{await action();}catch(Exception ex){StatusLabel.Text=ex.Message;}})));}
        Add("Открыть приложение",()=>RestoreFromTrayAsync());
        Add("Камера",async()=>{await RestoreFromTrayAsync(false);await OpenCameraTabAsync(false);await BusyAsync(()=>PlayAsync(),"Подключение камеры…");});
        Add("Архив",async()=>{await RestoreFromTrayAsync(false);await OpenArchiveTabAsync();});
        var doorItem=new Forms.ToolStripMenuItem("Открыть дверь");doorItem.Click+=(_,_)=>Dispatcher.BeginInvoke(()=>OpenDoorClick(this,new()));trayMenu.Items.Add(doorItem);
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        Add("Настройки",async()=>{await RestoreFromTrayAsync(false);await OpenSettingsTabAsync();});
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        Add("Выход",()=>{RequestExit();return Task.CompletedTask;});
        trayMenu.Opening+=(_,_)=>{doorItem.Enabled=api?.Session is not null&&Door?.AllowOpen==true&&!closing;};
        trayIcon=new Forms.NotifyIcon{Icon=trayImage,Text="Домофон ПК",ContextMenuStrip=trayMenu,Visible=true};
        trayIcon.DoubleClick+=(_,_)=>Dispatcher.BeginInvoke(new Action(async()=>await RestoreFromTrayAsync()));
        InitializeTrayHover();
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized&&!closing&&!closed)_=HideToTrayAsync();else if(WindowState!=WindowState.Minimized)trayRestoreState=WindowState;};
    }
    private void StartupChanged(object sender,RoutedEventArgs e)
    {
        if(!backgroundSettingsReady||IsDiagnosticRun)return;
        try{startupRegistration.SetEnabled(StartupBox.IsChecked==true,Environment.ProcessPath??throw new InvalidOperationException("Путь приложения недоступен"));StartupStatus.Text=StartupBox.IsChecked==true?"При входе в Windows приложение запускается в трее":"Автозагрузка отключена";}
        catch{backgroundSettingsReady=false;try{StartupBox.IsChecked=startupRegistration.IsEnabled();}catch{StartupBox.IsChecked=false;}backgroundSettingsReady=true;StartupStatus.Text="Не удалось изменить автозагрузку Windows";}
    }
    private void CloseToTrayChanged(object sender,RoutedEventArgs e)
    {
        if(CloseChromeButton is not null)CloseChromeButton.ToolTip=CloseToTrayBox.IsChecked==true?"Свернуть в трей":"Закрыть приложение";
        if(store is null)return;
        try{store.SaveCloseToTray(CloseToTrayBox.IsChecked==true);}catch{StatusLabel.Text="Не удалось сохранить настройку закрытия окна";}
    }
    private async Task HideToTrayAsync()
    {
        if(trayIcon is null||closing||closed)return;
        ExitFullscreen();if(WindowState!=WindowState.Minimized)trayRestoreState=WindowState;
        Hide();ShowInTaskbar=false;videoVersion++;videoRenewal?.Stop();
        if(video is not null)await video.StopAsync();
    }
    internal async Task RestoreFromTrayAsync(bool resumeCamera=true)
    {
        if(closing||closed)return;
        var restoring=!IsVisible||WindowState==WindowState.Minimized;
        ShowInTaskbar=true;Show();WindowState=trayRestoreState==WindowState.Maximized?WindowState.Maximized:WindowState.Normal;Activate();
        if(restoring&&video is not null)await video.StopAsync();
        if(restoring&&resumeCamera&&IsVisible)await AutoplayCameraAsync();
    }
    internal void RequestExit(){exitRequested=true;Close();}
    internal void AllowSystemExit()=>exitRequested=true;
    internal void DisposeTray()
    {
        trayHoverTimer?.Stop();trayHoverTimer=null;
        if(trayIcon is not null){trayIcon.Visible=false;trayIcon.Dispose();trayIcon=null;}
        trayMenu?.Dispose();trayMenu=null;trayImage?.Dispose();trayImage=null;
    }
    private async Task VerifyTrayAsync()
    {
        var original=CloseToTrayBox.IsChecked==true;
        try
        {
        CloseToTrayBox.IsChecked=true;
        if(trayIcon is null||!trayIcon.Visible)throw new InvalidOperationException("Значок трея не создан");
        Close();await Task.Delay(300);if(IsVisible||ShowInTaskbar||closed)throw new InvalidOperationException("Закрытие окна в трей не работает");
        await RestoreFromTrayAsync();if(!IsVisible||!ShowInTaskbar)throw new InvalidOperationException("Возврат из трея не работает");
        if(CameraAutoplayBox.IsChecked==true&&api?.Session is not null)
        {
            for(int i=0;i<100&&video?.Player.IsPlaying!=true;i++)await Task.Delay(100);
            if(video?.Player.IsPlaying!=true)throw new InvalidOperationException("Автовоспроизведение не восстановилось из трея");
        }
        var menu=trayMenu!.Items.OfType<Forms.ToolStripMenuItem>().Select(x=>x.Text??"").ToArray();
        await VerifyTrayHoverAsync();
        if(menu.Any(x=>x.Contains("Принимать звонки")))throw new InvalidOperationException("Лишний пункт меню трея");
        var root=Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR")??System.IO.Path.Combine(Environment.CurrentDirectory,"artifacts");System.IO.Directory.CreateDirectory(root);
        var result=new System.Text.Json.Nodes.JsonObject{["iconVisible"]=true,["hideKeepsAppRunning"]=true,["restoreWorks"]=true,["hoverAutoHide"]=true,["livePlayingAfterRestore"]=video?.Player.IsPlaying==true,["menuItems"]=System.Text.Json.JsonSerializer.SerializeToNode(menu),["startupEnabled"]=StartupBox.IsChecked==true};
        CloseToTrayBox.IsChecked=false;if(store!.LoadCloseToTray())throw new InvalidOperationException("Настройка закрытия не сохранена");
        Closed+=(_,_)=>{result["closedWhenDisabled"]=true;result["trayDisposed"]=trayIcon is null;System.IO.File.WriteAllText(System.IO.Path.Combine(root,"tray-verification.json"),result.ToJsonString());};
        Close();
        }
        finally{store?.SaveCloseToTray(original);}
    }
}
