using LibVLCSharp.Shared;

namespace Domru.Desktop.Core;
public sealed class VideoPlayer : IAsyncDisposable
{
    private readonly LibVLC vlc;
    public MediaPlayer Player { get; }
    private readonly SemaphoreSlim lifecycle=new(1,1);
    private bool disposed;

    public event Action<string>? State;
    public VideoPlayer()
    {
        LibVLCSharp.Shared.Core.Initialize();
        // Snapshots must not add a thumbnail or a temporary filename to the video.
        vlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-osd");
        Player = new MediaPlayer(vlc);
        Player.EnableMouseInput=false;
        Player.EnableKeyInput=false;
        Player.Playing += (_, _) => State?.Invoke("Воспроизведение");
        Player.EncounteredError += (_, _) => State?.Invoke("Ошибка воспроизведения — обновите ссылку потока");
        Player.EndReached += (_, _) => State?.Invoke("Запись закончилась");
    }

    public async Task PlayAsync(string url,IntPtr host, bool mute = false, bool live = true, bool minimumLatency = true,bool repeat=false)
    {
        if(host==IntPtr.Zero)throw new InvalidOperationException("Видеополе ещё не готово к воспроизведению");
        await lifecycle.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed,this);
        await Task.Run(()=>Player.Stop());
        using var media = new Media(vlc, new Uri(url));
        if(repeat)media.AddOption(":input-repeat=65535");
        media.AddOption(live ? (minimumLatency ? ":network-caching=50" : ":network-caching=150") : ":network-caching=700");
        if (live)
        {
            media.AddOption(minimumLatency ? ":live-caching=50" : ":live-caching=150");
            media.AddOption(":clock-jitter=0");
            if (minimumLatency) media.AddOption(":clock-synchro=0");
        }
        Player.Mute = mute;
        Player.Hwnd=host;
        if (!await Task.Run(()=>Player.Play(media)))
            throw new InvalidOperationException("Не удалось запустить видеопоток");
        } finally { lifecycle.Release(); }
    }
    public async Task<byte[]> SnapshotAsync()
    {
        await lifecycle.WaitAsync();
        var path=Path.Combine(Path.GetTempPath(),$"domru-frame-{Guid.NewGuid():N}.png");
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<MediaPlayerSnapshotTakenEventArgs> handler=(_,e)=>{if(string.Equals(e.Filename,path,StringComparison.OrdinalIgnoreCase))ready.TrySetResult();};
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            Player.SnapshotTaken+=handler;
            // Zero dimensions preserve the decoded frame's native resolution.
            if(!Player.TakeSnapshot(0,path,0,0))throw new InvalidOperationException("Нет видеокадра. Сначала запустите просмотр камеры или записи.");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            Player.SnapshotTaken-=handler;
            if(File.Exists(path))File.Delete(path);
            lifecycle.Release();
        }
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();try{if(!disposed)await Task.Run(()=>Player.Stop());}finally{lifecycle.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();try{if(disposed)return;disposed=true;await Task.Run(()=>{Player.Stop();Player.Dispose();vlc.Dispose();});}finally{lifecycle.Release();}
    }
}
