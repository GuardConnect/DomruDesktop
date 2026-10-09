using NAudio.Wave;
namespace Domru.Desktop.Core;
public static class AudioAssets
{
    public static WaveFileReader OpenRingtone()
    {
        var stream=typeof(AudioAssets).Assembly.GetManifestResourceStream("DomruDesktop.Audio.Ringtone.wav")??throw new InvalidOperationException("Встроенный рингтон не найден");
        try{return new ResourceWaveReader(stream);}catch{stream.Dispose();throw;}
    }
    private sealed class ResourceWaveReader:WaveFileReader
    {
        private readonly Stream resource;
        public ResourceWaveReader(Stream resource):base(resource){this.resource=resource;}
        protected override void Dispose(bool disposing){try{base.Dispose(disposing);}finally{if(disposing)resource.Dispose();}}
    }
}
