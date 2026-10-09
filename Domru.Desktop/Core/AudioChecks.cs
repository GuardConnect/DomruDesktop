using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Domru.Desktop.Core;

public sealed record MicrophoneRecording(byte[] Pcm,int Peak,int Channels,int CapturedBytes);

public static class AudioChecks
{
    public static byte[] PrepareVoicePlayback(byte[] pcm)
    {
        var peak=0;for(int i=0;i+1<pcm.Length;i+=2)peak=Math.Max(peak,Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i))));
        var gain=peak>=64?Math.Clamp(12000.0/peak,1,8):1;var result=new byte[pcm.Length];
        for(int i=0;i+1<pcm.Length;i+=2){var sample=BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i));BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i),(short)Math.Clamp((int)Math.Round(sample*gain),short.MinValue,short.MaxValue));}
        return result;
    }
    public static int InputChannels(int device)
    {
        if(device>=0)return Math.Clamp(WaveIn.GetCapabilities(device).Channels,1,2);
        try{using var enumeration=new MMDeviceEnumerator();using var input=enumeration.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia);return Math.Clamp(input.AudioClient.MixFormat.Channels,1,2);}
        catch{return 1;}
    }
    public static MicrophoneRecording Normalize(byte[] pcm,int channels)
    {
        if(channels is <1 or >2)throw new ArgumentOutOfRangeException(nameof(channels));
        var frames=pcm.Length/(channels*2);var energy=new long[channels];
        for(int frame=0;frame<frames;frame++)for(int channel=0;channel<channels;channel++){var sample=BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan((frame*channels+channel)*2));energy[channel]+=(long)sample*sample;}
        var selected=channels==2&&energy[1]>energy[0]?1:0;var result=new byte[frames*2];var peak=0;
        for(int frame=0;frame<frames;frame++){var sample=BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan((frame*channels+selected)*2));BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(frame*2),sample);peak=Math.Max(peak,Math.Abs((int)sample));}
        return new(result,peak,channels,pcm.Length);
    }
}

public sealed class MicrophoneRecorder : IDisposable
{
    private readonly WaveInEvent capture;
    private readonly MemoryStream recording=new();
    private readonly object sync=new();
    private readonly int channels;
    private bool disposed;
    public event Action<int>? Level;
    public Exception? Error {get;private set;}
    public MicrophoneRecorder(int device)
    {
        channels=AudioChecks.InputChannels(device);
        capture=new WaveInEvent{DeviceNumber=device,WaveFormat=new WaveFormat(48000,16,channels),BufferMilliseconds=20};
        capture.DataAvailable+=(_,e)=>{
            var peak=0;for(int i=0;i+1<e.BytesRecorded;i+=2)peak=Math.Max(peak,Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(e.Buffer.AsSpan(i))));
            lock(sync){if(!disposed&&recording.Length<48000*channels*2*6)recording.Write(e.Buffer,0,e.BytesRecorded);}
            Level?.Invoke(Math.Min(100,peak*100/12000));
        };
        capture.RecordingStopped+=(_,e)=>Error=e.Exception;
    }
    public void Start()=>capture.StartRecording();
    public MicrophoneRecording Finish()
    {
        capture.StopRecording();lock(sync)return AudioChecks.Normalize(recording.ToArray(),channels);
    }
    public void Dispose()
    {
        lock(sync){if(disposed)return;disposed=true;}
        capture.StopRecording();capture.Dispose();recording.Dispose();
    }
}

public sealed class AudioPlayback : IDisposable
{
    private readonly WaveOutEvent output;
    private readonly WaveStream source;
    private bool disposed;
    public event Action<Exception?>? Finished;
    private AudioPlayback(WaveStream source,int device,bool repeat,float gain=1)
    {
        this.source=source;output=new WaveOutEvent{DeviceNumber=device,DesiredLatency=100,Volume=gain>1?1f:0.65f};
        try{IWaveProvider provider=repeat?new RepeatingAudio(source):source;if(gain>1)provider=new VolumeWaveProvider16(provider){Volume=gain};output.Init(provider);output.PlaybackStopped+=(_,e)=>Finished?.Invoke(e.Exception);}
        catch{output.Dispose();source.Dispose();throw;}
    }
    public static AudioPlayback File(string path,int device=-1,bool repeat=false)=>new(new WaveFileReader(path),device,repeat);
    public static AudioPlayback Ringtone(int device=-1)=>new(AudioAssets.OpenRingtone(),device,true,3f);
    public static AudioPlayback Pcm(byte[] bytes,int device=-1)=>new(new RawSourceWaveStream(new MemoryStream(AudioChecks.PrepareVoicePlayback(bytes),false),new WaveFormat(48000,16,1)),device,false);
    public void Play()=>output.Play();
    public void Dispose(){if(disposed)return;disposed=true;output.Stop();output.Dispose();source.Dispose();}
    private sealed class RepeatingAudio(WaveStream source) : IWaveProvider
    {
        public WaveFormat WaveFormat=>source.WaveFormat;
        public int Read(byte[] buffer,int offset,int count)
        {
            var total=0;while(total<count){var read=source.Read(buffer,offset+total,count-total);if(read==0){if(source.Length==0)break;source.Position=0;}else total+=read;}return total;
        }
    }
}
