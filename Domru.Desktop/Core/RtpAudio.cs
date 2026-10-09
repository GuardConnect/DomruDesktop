using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using NAudio.Codecs;
using NAudio.Wave;

namespace Domru.Desktop.Core;
public sealed class RtpAudio : IAsyncDisposable
{
    private readonly UdpClient udp = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> microphone = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(15) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private WaveInEvent? input;
    private WaveOutEvent? output;
    private BufferedWaveProvider? speaker;
    private Task? receive, send;
    private IPEndPoint? remote;
    private int payload;
    private string codec = "";
    private ushort sequence = (ushort)RandomNumberGenerator.GetInt32(65536);
    private uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
    private readonly uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
    private int? lastReceiveSequence;
    private MicrophoneUplink? uplink;
    private long capturedBytes,speechPackets;
    private int microphoneLevel;
    private string? captureError;
    public AudioTransmissionStats Stats=>new(Interlocked.Read(ref capturedBytes),Volatile.Read(ref microphoneLevel),SentPackets,Interlocked.Read(ref speechPackets),ReceivedPackets,Muted,captureError);
    public bool Muted { get; set; }
    public IPEndPoint PublicEndpoint { get; private set; } = new(IPAddress.Any, 0);

    public event Action<string>? Status;
    public int InputDevice { get; set; } = -1;
    public int OutputDevice { get; set; } = -1;
    internal bool UseAudioDevices { get; set; } = true;
    internal long ReceivedPackets { get; private set; }
    internal long SentPackets { get; private set; }
    internal byte[]? LastPcm { get; private set; }
    internal bool EnableStun { get; set; } = true;

    public async Task PrepareAsync(IPAddress localIp, CancellationToken ct = default)
    {
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        PublicEndpoint = new(localIp, ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
        if (!EnableStun)
            return;
        try
        {
            var ip = (await Dns.GetHostAddressesAsync("stun.l.google.com", ct)).First(x => x.AddressFamily == AddressFamily.InterNetwork);
            var request = SipProtocol.StunRequest();
            await udp.SendAsync(request, new IPEndPoint(ip, 19302), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(2500);
            var r = await udp.ReceiveAsync(timeout.Token);
            if (r.RemoteEndPoint.Address.Equals(ip) && r.Buffer.AsSpan(8, 12).SequenceEqual(request.AsSpan(8, 12)))
                PublicEndpoint = SipProtocol.ParseStun(r.Buffer) ?? PublicEndpoint;
        }
        catch (Exception e)when (e is SocketException or OperationCanceledException)
        {
            Status?.Invoke("RTP: STUN недоступен, используется локальный адрес");
        }
    }

    public void Start(IPEndPoint remote, int payload, string codec)
    {
        this.remote = remote;
        this.payload = payload;
        this.codec = codec;
        if (UseAudioDevices)
        {
            speaker = new BufferedWaveProvider(new WaveFormat(8000, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true
            };
            output = new WaveOutEvent
            {
                DeviceNumber = OutputDevice,
                DesiredLatency = 100
            };
            output.Init(speaker);
            output.Play();
            input = new WaveInEvent
            {
                DeviceNumber = InputDevice,
                WaveFormat = new WaveFormat(48000, 16, AudioChecks.InputChannels(InputDevice)),
                BufferMilliseconds = 20,
                NumberOfBuffers = 3
            };
            input.DataAvailable += (_, e) =>
            {
                var copy = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, copy, 0, copy.Length);
                SubmitCapturedPcm(copy,48000,input.WaveFormat.Channels);
            };
            input.RecordingStopped+=(_,e)=>{if(e.Exception is not null&&!stop.IsCancellationRequested){captureError=e.Exception.Message;Status?.Invoke("Микрофон: "+captureError);}};
            input.StartRecording();
        }

        receive = ReceiveAsync(stop.Token);
        send = SendAsync(stop.Token);
    }

    internal void SubmitCapturedPcm(byte[] pcm,int rate,int channels)
    {
        uplink??=new(rate,channels);var converted=uplink.Convert(pcm);Interlocked.Add(ref capturedBytes,pcm.Length);Volatile.Write(ref microphoneLevel,uplink.Level);
        if(converted.Length>0)microphone.Writer.TryWrite(converted);
    }

    private async Task SendAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var pending = new Queue<byte>();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                while (microphone.Reader.TryRead(out var pcm))
                    foreach (var b in pcm)
                        pending.Enqueue(b);
                if (pending.Count > 3200)
                    while (pending.Count > 640)
                        pending.Dequeue();
                var packet = new byte[172];
                packet[0] = 0x80;
                packet[1] = (byte)payload;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
                timestamp += 160;
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
                var peak=0;
                for (int i = 0; i < 160; i++)
                {
                    short sample = 0;
                    if (pending.Count >= 2)
                    {
                        var lo = pending.Dequeue();
                        var hi = pending.Dequeue();
                        sample = (short)(lo | (hi << 8));
                    }

                    if (Muted)
                        sample = 0;
                    peak=Math.Max(peak,Math.Abs((int)sample));
                    packet[12 + i] = codec.StartsWith("PCMA") ? ALawEncoder.LinearToALawSample(sample) : MuLawEncoder.LinearToMuLawSample(sample);
                }

                if (remote is { } target)
                {
                    await udp.SendAsync(packet, target, ct);
                    SentPackets++;
                    if(peak>64)Interlocked.Increment(ref speechPackets);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
            Status?.Invoke("RTP: ошибка отправки звука");
        }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(ct);
                if (remote is null || !r.RemoteEndPoint.Address.Equals(remote.Address))
                    continue;
                var b = r.Buffer;
                if (b.Length < 12 || (b[0] >> 6) != 2 || (b[1] & 127) != payload)
                    continue;
                var offset = 12 + 4 * (b[0] & 15);
                if (offset > b.Length)
                    continue;
                if ((b[0] & 16) != 0)
                {
                    if (offset + 4 > b.Length)
                        continue;
                    offset += 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(offset + 2));
                }

                var end = b.Length;
                if ((b[0] & 32) != 0)
                    end -= b[^1];
                if (offset >= end)
                    continue;
                var seq = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(2));
                if (lastReceiveSequence is int last && (short)(seq - last) <= 0)
                    continue;
                lastReceiveSequence = seq;
                remote = r.RemoteEndPoint;
                var pcm = new byte[(end - offset) * 2];
                for (int i = offset; i < end; i++)
                {
                    short s = codec.StartsWith("PCMA") ? ALawDecoder.ALawToLinearSample(b[i]) : MuLawDecoder.MuLawToLinearSample(b[i]);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i - offset) * 2), s);
                }

                LastPcm = pcm;
                ReceivedPackets++;
                speaker?.AddSamples(pcm, 0, pcm.Length);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
            Status?.Invoke("RTP: ошибка приёма звука");
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        input?.StopRecording();
        input?.Dispose();
        output?.Stop();
        output?.Dispose();
        udp.Dispose();
        foreach (var t in new[]
        {
            send,
            receive
        }

        )
            if (t is not null)
                try
                {
                    await t;
                }
                catch (ObjectDisposedException)
                {
                }

        stop.Dispose();
    }
}
