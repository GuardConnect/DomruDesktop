using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Domru.Desktop.Core;
public sealed class SessionStore
{
    private readonly string directory;
    public string InstallationId { get; }
    public (string? Microphone,string? Speaker) LoadAudioDevices()
    {
        try{var value=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(directory,"audio-devices.json")));return (Json.Str(value,"microphone"),Json.Str(value,"speaker"));}
        catch(Exception ex)when(ex is IOException or JsonException){return (null,null);}
    }
    public void SaveAudioDevices(string? microphone,string? speaker)
        =>File.WriteAllText(Path.Combine(directory,"audio-devices.json"),new System.Text.Json.Nodes.JsonObject{["microphone"]=microphone,["speaker"]=speaker}.ToJsonString());

    public SessionStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DomruDesktop");
        Directory.CreateDirectory(this.directory);
        var path = Path.Combine(this.directory, "installation.id");
        InstallationId = File.Exists(path) ? File.ReadAllText(path).Trim() : Guid.NewGuid().ToString();
        if (!Guid.TryParse(InstallationId, out _))
            InstallationId = Guid.NewGuid().ToString();
        File.WriteAllText(path, InstallationId);
    }

    public AuthSession? Load()
    {
        var path = Path.Combine(directory, "session.dpapi");
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AuthSession>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
    public bool LoadCameraAutoplay()
    {
        var path=Path.Combine(directory,"camera-autoplay.json");
        try{return !File.Exists(path)||JsonSerializer.Deserialize<bool>(File.ReadAllText(path));}
        catch(JsonException){return true;}
        catch(IOException){return true;}
    }
    public void SaveCameraAutoplay(bool enabled)
    {
        var path=Path.Combine(directory,"camera-autoplay.json");
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(enabled));File.Move(path+".tmp",path,true);
    }
    public bool LoadCloseToTray()
    {
        var path=Path.Combine(directory,"close-to-tray.json");
        try{return !File.Exists(path)||JsonSerializer.Deserialize<bool>(File.ReadAllText(path));}
        catch(JsonException){return true;}catch(IOException){return true;}
    }
    public void SaveCloseToTray(bool enabled)
    {
        var path=Path.Combine(directory,"close-to-tray.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(enabled));File.Move(path+".tmp",path,true);
    }

    public void Save(AuthSession? session)
    {
        var path = Path.Combine(directory, "session.dpapi");
        if (session is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session), null, DataProtectionScope.CurrentUser);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        File.Move(tmp, path, true);
    }

    public byte[]? LoadPush()
    {
        var path = Path.Combine(directory, "push.dpapi");
        if (!File.Exists(path))
            return null;
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void SavePush(byte[] data)
    {
        var path = Path.Combine(directory, "push.dpapi");
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true);
    }
}
