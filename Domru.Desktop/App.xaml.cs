using System.Windows;
using System.Security.Principal;

namespace Domru.Desktop;
public partial class App : Application
{
    private Mutex? singleInstance;
    private EventWaitHandle? activationSignal;
    private RegisteredWaitHandle? activationWait;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SessionEnding+=(_,_)=>{if(MainWindow is MainWindow main)main.AllowSystemExit();};
        if (!e.Args.Any(a => a is "--verify-window-chrome" or "--smoke" or "--verify-live" or "--verify-video" or "--verify-archive" or "--verify-cycle" or "--verify-services" or "--verify-autoplay" or "--verify-tray" or "--verify-audio-checks" or "--verify-archive-events"))
        {
            var user=WindowsIdentity.GetCurrent().User?.Value;
            singleInstance = new Mutex(true, @"Local\DomruDesktop." + user, out var created);
            if (!created)
            {
                try{using var signal=EventWaitHandle.OpenExisting(@"Local\DomruDesktop.Activate."+user);signal.Set();}
                catch(WaitHandleCannotBeOpenedException){MessageBox.Show("Домофон ПК уже запущен. Откройте существующее окно или значок в трее.","Домофон ПК");}
                Shutdown();
                return;
            }
            activationSignal=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\DomruDesktop.Activate."+user);
            activationWait=ThreadPool.RegisterWaitForSingleObject(activationSignal,(_,_)=>Dispatcher.BeginInvoke(new Action(async()=>{if(MainWindow is MainWindow main)await main.RestoreFromTrayAsync();})),null,Timeout.Infinite,false);
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Домофон ПК");
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.DisposeTray();
        activationWait?.Unregister(null);activationSignal?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}


