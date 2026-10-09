using System.Buffers.Binary;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace Domru.Desktop.Core;

internal sealed class MicrophoneUplink(int rate,int channels)
{
    private readonly BufferedWaveProvider source=new(new WaveFormat(rate,16,1)){ReadFully=false,DiscardOnBufferOverflow=true,BufferDuration=TimeSpan.FromMilliseconds(300)};
    private WdlResamplingSampleProvider? resampler;
    public int Level {get;private set;}
    public byte[] Convert(byte[] pcm)
    {
        var mono=AudioChecks.Normalize(pcm,channels);Level=Math.Min(100,mono.Peak*100/12000);
        if(rate==8000)return mono.Pcm;
        source.AddSamples(mono.Pcm,0,mono.Pcm.Length);resampler??=new(source.ToSampleProvider(),8000);
        var floats=new float[Math.Max(1,mono.Pcm.Length/2*8000/rate)];var read=resampler.Read(floats,0,floats.Length);var result=new byte[read*2];
        for(int i=0;i<read;i++)BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i*2),(short)Math.Clamp((int)Math.Round(floats[i]*32767),short.MinValue,short.MaxValue));
        return result;
    }
}
public sealed record AudioTransmissionStats(long CapturedBytes,int MicrophoneLevel,long SentPackets,long SpeechPackets,long ReceivedPackets,bool Muted,string? CaptureError);
