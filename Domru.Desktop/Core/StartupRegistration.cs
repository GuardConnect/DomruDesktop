using Microsoft.Win32;
namespace Domru.Desktop.Core;
public sealed class StartupRegistration
{
    public const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string keyPath,valueName;
    public StartupRegistration(string keyPath=RunKey,string valueName="DomruDesktop"){this.keyPath=keyPath;this.valueName=valueName;}
    public bool IsEnabled(){using var key=Registry.CurrentUser.OpenSubKey(keyPath);return key?.GetValue(valueName) is string {Length:>0};}
    public static string Command(string executable)
    {
        if(!Path.IsPathFullyQualified(executable)||executable.Contains('"')||!executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Требуется полный путь к приложению",nameof(executable));
        return "\""+executable+"\" --tray";
    }
    public void SetEnabled(bool enabled,string executable)
    {
        if(enabled){using var key=Registry.CurrentUser.CreateSubKey(keyPath);key.SetValue(valueName,Command(executable),RegistryValueKind.String);}
        else{using var key=Registry.CurrentUser.OpenSubKey(keyPath,true);key?.DeleteValue(valueName,false);}
    }
}
