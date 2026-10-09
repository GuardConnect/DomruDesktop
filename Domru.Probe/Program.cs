using Domru.Desktop.Core;
using System.Text.Json.Nodes;

var store = new SessionStore(Path.GetFullPath(".private"));
using var api = new DomruApi(store.InstallationId, store.Load());
api.SessionChanged += store.Save;
if (api.Session is null)
{
    var input = JsonNode.Parse(Console.ReadLine() ?? "{}");
    await api.PasswordLoginAsync(input?["login"]?.ToString() ?? "", input?["password"]?.ToString() ?? "");
}

Console.WriteLine("AUTH OK (credentials omitted)");
if(args.Contains("--verify-microphone-rtp")||args.Contains("--verify-microphone-sip"))
{
    var settings=new SessionStore().LoadAudioDevices();var mic=-1;var speaker=-1;
    for(int i=0;i<NAudio.Wave.WaveIn.DeviceCount;i++)if(NAudio.Wave.WaveIn.GetCapabilities(i).ProductName==settings.Microphone)mic=i;
    for(int i=0;i<NAudio.Wave.WaveOut.DeviceCount;i++)if(NAudio.Wave.WaveOut.GetCapabilities(i).ProductName==settings.Speaker)speaker=i;
    Console.WriteLine("MIC="+(mic<0?"Windows default":NAudio.Wave.WaveIn.GetCapabilities(mic).ProductName));
    if(args.Contains("--verify-microphone-sip"))
    {
        using var signaling=new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0));
        using var media=new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0));using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var door=new Door(Guid.NewGuid().ToString(),"local-mic-test","Local microphone test","SIP",null,null,"LOCAL_TEST",false,false,10800,new());
        await using var client=new SipClient(new("local-test","local-only","localhost"),door){ServerOverride=(System.Net.IPEndPoint)signaling.Client.LocalEndPoint!,EnableStun=false,InputDevice=mic,OutputDevice=speaker,AudioFactory=()=>new(){EnableStun=false}};
        var incoming=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);client.Incoming+=_=>incoming.TrySetResult();await client.StartAsync();
        var register=await signaling.ReceiveAsync(deadline.Token);var request=SipMessage.Parse(System.Text.Encoding.UTF8.GetString(register.Buffer));
        await signaling.SendAsync(System.Text.Encoding.UTF8.GetBytes(request.Response(200,"OK","local-server")),register.RemoteEndPoint,deadline.Token);
        var callId=Guid.NewGuid().ToString();var mediaPort=((System.Net.IPEndPoint)media.Client.LocalEndPoint!).Port;
        var body=$"v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {mediaPort} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
        var invite=$"INVITE sip:local-test@localhost SIP/2.0\r\nVia: SIP/2.0/UDP 127.0.0.1;branch=z9hG4bKlocaltest\r\nFrom: <sip:caller@localhost>;tag=caller\r\nTo: <sip:local-test@localhost>\r\nCall-ID: {callId}\r\nCSeq: 1 INVITE\r\nContact: <sip:caller@localhost>\r\nContent-Type: application/sdp\r\nContent-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        await signaling.SendAsync(System.Text.Encoding.UTF8.GetBytes(invite),register.RemoteEndPoint,deadline.Token);await incoming.Task.WaitAsync(deadline.Token);
        Console.WriteLine("BEFORE ANSWER microphoneInactive="+(client.AudioStats is null));await client.AnswerAsync();SipMessage answer;
        do{var packet=await signaling.ReceiveAsync(deadline.Token);answer=SipMessage.Parse(System.Text.Encoding.UTF8.GetString(packet.Buffer));}while(!answer.FirstLine.StartsWith("SIP/2.0 200")||answer.Get("cseq")!="1 INVITE");
        var ack=$"ACK sip:local-test@localhost SIP/2.0\r\nFrom: {answer.Get("from")}\r\nTo: {answer.Get("to")}\r\nCall-ID: {callId}\r\nCSeq: 1 ACK\r\nContent-Length: 0\r\n\r\n";
        await signaling.SendAsync(System.Text.Encoding.UTF8.GetBytes(ack),register.RemoteEndPoint,deadline.Token);
        var sipPeak=0;var sipPackets=0;using var duration=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try{while(true){var packet=await media.ReceiveAsync(duration.Token);sipPackets++;foreach(var value in packet.Buffer.Skip(12))sipPeak=Math.Max(sipPeak,Math.Abs((int)NAudio.Codecs.ALawDecoder.ALawToLinearSample(value)));}}catch(OperationCanceledException){}
        Console.WriteLine($"MIC SIP RESULT confirmed={client.ConversationConfirmed} packets={sipPackets} decodedPeak={sipPeak} capturedBytes={client.AudioStats?.CapturedBytes} speechPackets={client.AudioStats?.SpeechPackets}");return;
    }
    using var sink=new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0));
    await using var audio=new RtpAudio{EnableStun=false,InputDevice=mic,OutputDevice=speaker};audio.Status+=Console.WriteLine;
    await audio.PrepareAsync(System.Net.IPAddress.Loopback);audio.Start((System.Net.IPEndPoint)sink.Client.LocalEndPoint!,8,"PCMA/8000");
    var peak=0;var packets=0;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try{while(true){var received=await sink.ReceiveAsync(timeout.Token);packets++;foreach(var value in received.Buffer.Skip(12))peak=Math.Max(peak,Math.Abs((int)NAudio.Codecs.ALawDecoder.ALawToLinearSample(value)));}}catch(OperationCanceledException){}
    Console.WriteLine($"MIC RTP RESULT packets={packets} decodedPeak={peak} capturedBytes={audio.Stats.CapturedBytes} speechPackets={audio.Stats.SpeechPackets}");return;
}
if(args.Contains("--services"))
{
    var place=(await api.PlacesAsync())[0];
    foreach(var entry in new (string Name,Func<Task<JsonNode?>> Read)[]{("keys",()=>api.AccessKeysAsync(place.Id)),("finance",()=>api.FinanceAsync(place.Id)),("payment",()=>api.PaymentInfoAsync(place.Id)),("profile",api.ProfileAsync)})
    {
        try{
            var data=Json.Data(await entry.Read());
            Console.WriteLine(entry.Name+" shape="+(data is JsonObject obj?string.Join(',',obj.Select(p=>p.Key)):data?.GetType().Name));
            if(data is JsonArray arr)Console.WriteLine(entry.Name+" count="+arr.Count+" itemKeys="+(arr.FirstOrDefault() is JsonObject first?string.Join(',',first.Select(p=>p.Key)):"none"));
            if(data is JsonObject o)foreach(var field in o.Where(p=>p.Value is JsonObject))Console.WriteLine(entry.Name+"."+field.Key+" keys="+string.Join(',',((JsonObject)field.Value!).Select(p=>p.Key)));
            var link=Json.Str(data,"paymentLink");if(Uri.TryCreate(link,UriKind.Absolute,out var uri))Console.WriteLine(entry.Name+" paymentHost="+uri.Host);
        }catch(Exception ex){Console.WriteLine(entry.Name+" unavailable: "+ex.GetType().Name);}
    }
    return;
}
if(args.Contains("--captions"))
{
    var place=(await api.PlacesAsync())[0];var door=(await api.DoorsAsync(place.Id)).First(d=>d.CameraId is not null);
    var rows=await ArchiveHistory.LoadAsync(api,door,DateTimeOffset.Now);
    foreach(var row in rows.GroupBy(x=>x.Title).Select(g=>g.First()).Take(8))
        Console.WriteLine($"TITLE={row.Title} KEYS={string.Join(',',row.Raw.Select(x=>x.Key))} DISPLAY={EventCaption.ArchiveTime(row)} | {EventCaption.ArchiveTitle(row)}");
    return;
}
if(args.Contains("--options")){Console.WriteLine(string.Join(",",typeof(LibVLCSharp.Shared.Media).GetMethods().Where(m=>m.Name.Contains("Option")).Select(m=>m.Name)));return;}
if(args.Contains("--startup")||args.Contains("--startup-min"))
{
    var place=(await api.PlacesAsync())[0];var door=(await api.DoorsAsync(place.Id)).First(d=>d.CameraId is not null);
    LibVLCSharp.Shared.Core.Initialize();
    foreach(var aggressive in new[]{args.Contains("--startup-min")})
    {
        var options=new List<string>{"--vout=dummy","--aout=dummy","--avcodec-hw=none","--clock-jitter=0",aggressive?"--network-caching=50":"--network-caching=150"};
        if(aggressive){options.Add("--clock-synchro=0");}
        using var vlc=new LibVLCSharp.Shared.LibVLC(options.ToArray());using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
        var url=await api.VideoAsync(door.CameraId!);using var media=new LibVLCSharp.Shared.Media(vlc,new Uri(url));var timer=System.Diagnostics.Stopwatch.StartNew();player.Play(media);
        while(media.Statistics.DecodedVideo==0&&timer.ElapsedMilliseconds<12000)await Task.Delay(20);
        Console.WriteLine($"PROFILE={(aggressive?"minimum":"standard")} FIRST_DECODE_MS={timer.ElapsedMilliseconds} frames={media.Statistics.DecodedVideo}");await Task.Delay(6000);player.Stop();
    }
    return;
}
if(args.Contains("--archive4")){var place=(await api.PlacesAsync())[0];var door=(await api.DoorsAsync(place.Id))[0];var now=DateTimeOffset.Now;var sample=await api.SearchEventsAsync(place.Id,0,ArchiveHistory.FourDayStart(now),now);Console.WriteLine($"PERIOD page0={sample.Events.Count} last={sample.Last}");var unfiltered=await api.SearchEventsAsync(place.Id);Console.WriteLine("UNFILTERED dates="+string.Join(',',unfiltered.Events.Take(3).Select(x=>x.Time?.ToString("O")??"null")));foreach(var e in sample.Events.Take(3)){var source=Json.Str(e.Raw["source"],"id");Console.WriteLine($"EVENT date={e.Time:O} sourceType={Json.Str(e.Raw["source"],"type")} doorMatch={source==door.Id} cameraMatch={source==door.CameraId} externalMatch={source==door.ExternalDeviceId}");}var rows=await ArchiveHistory.LoadAsync(api,door,now);Console.WriteLine($"ARCHIVE4 count={rows.Count} from={ArchiveHistory.FourDayStart(now):O} to={now:O}");Console.WriteLine("SOURCE TYPES: "+string.Join(',',rows.Select(x=>Json.Str(x.Raw["source"],"type")??"unknown").Distinct()));Console.WriteLine("DATE GROUPS: "+string.Join(',',rows.GroupBy(x=>x.Time!.Value.LocalDateTime.Date).Select(g=>$"{g.Key:yyyy-MM-dd}:{g.Count()}")));return;}
if(args.Contains("--burst"))
{
    var place=(await api.PlacesAsync())[0];var door=(await api.DoorsAsync(place.Id)).First(d=>d.CameraId is not null);
    var url=await api.VideoAsync(door.CameraId!);using var h=new HttpClient();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(12));
    using var r=await h.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct.Token);await using var s=await r.Content.ReadAsStreamAsync(ct.Token);
    var head=new byte[13];await s.ReadExactlyAsync(head,ct.Token);var watch=System.Diagnostics.Stopwatch.StartNew();
    var tags=new List<(long Arrival,uint Timestamp,bool Key)>();
    while(watch.ElapsedMilliseconds<5500){var header=new byte[11];await s.ReadExactlyAsync(header,ct.Token);var len=(header[1]<<16)|(header[2]<<8)|header[3];if(len>2000000)throw new Exception("Invalid FLV tag");var payload=new byte[len+4];await s.ReadExactlyAsync(payload,ct.Token);var ts=((uint)header[7]<<24)|((uint)header[4]<<16)|((uint)header[5]<<8)|header[6];if(header[0]==9&&len>1&&payload[1]==1)tags.Add((watch.ElapsedMilliseconds,ts,(payload[0]>>4)==1));}
    foreach(var ms in new[]{250,500,1000,5500}){var subset=tags.Where(t=>t.Arrival<=ms).ToList();Console.WriteLine($"ARRIVAL <= {ms} ms: video packets={subset.Count}, video timestamp span={(subset.Count>1?subset[^1].Timestamp-subset[0].Timestamp:0)} ms");}
    Console.WriteLine("KEYFRAME timestamps relative: "+string.Join(',',tags.Where(t=>t.Key).Select(t=>t.Timestamp-tags[0].Timestamp)));
    return;
}
if(args.Contains("--latency"))
{
    var place=(await api.PlacesAsync())[0];var door=(await api.DoorsAsync(place.Id)).First(d=>d.CameraId is not null);
    using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};
    foreach(var format in new[]{"RTSP","H264","RTMP",""})
    {
        try{
            var response=Json.Data(await api.RequestAsync($"rest/v1/forpost/cameras/{Uri.EscapeDataString(door.CameraId!)}/video?LightStream=0"+(format.Length>0?"&Format="+format:"")));
            var url=Json.Str(response,"URL","url");if(url is null){Console.WriteLine(format+" no URL");continue;}
            using var streamResponse=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead);await using var stream=await streamResponse.Content.ReadAsStreamAsync();var bytes=new byte[4096];var count=await stream.ReadAsync(bytes);var text=System.Text.Encoding.UTF8.GetString(bytes,0,count);
            Console.WriteLine($"FORMAT={format} HTTP={(int)streamResponse.StatusCode} signature={Convert.ToHexString(bytes.AsSpan(0,Math.Min(8,count)))}");
            if(text.StartsWith("#EXTM3U")){Console.WriteLine(string.Join('\n',text.Split('\n').Where(l=>l.StartsWith("#EXT-X-TARGETDURATION")||l.StartsWith("#EXTINF")||l.StartsWith("#EXT-X-PROGRAM-DATE-TIME")||l.StartsWith("#EXT-X-MEDIA-SEQUENCE"))));}
        }catch(Exception ex){Console.WriteLine(format+": "+ex.Message);}
    }
    return;
}
if (args.Contains("--history"))
{
    var p = (await api.PlacesAsync())[0];
    for (int i = 0; i < 2; i++)
    {
        var page = await api.SearchEventsAsync(p.Id, i);
        Console.WriteLine($"SEARCH page={i} count={page.Events.Count} last={page.Last}");
    }

    var d = (await api.DoorsAsync(p.Id)).First(x => x.CameraId is not null);
    var from = DateTimeOffset.UtcNow.AddDays(-1);
    var rows = DomruApi.ParseEvents(await api.CameraEventsAsync(d.CameraId!, from, DateTimeOffset.UtcNow));
    Console.WriteLine("CAMERA page1=" + rows.Count);
    if (rows.Any(x => x.Time is not null))
    {
        var to = rows.Where(x => x.Time is not null).Min(x => x.Time!.Value).AddMilliseconds(-1);
        var next = DomruApi.ParseEvents(await api.CameraEventsAsync(d.CameraId!, from, to));
        Console.WriteLine("CAMERA page2=" + next.Count + " overlapping=" + next.Count(x => rows.Any(y => x.Id == y.Id)));
    }

    return;
}

if (args.Contains("--push"))
{
    await using var push = new FcmReceiver(store);
    push.Status += Console.WriteLine;
    await push.PrepareAsync();
    push.Message += p => Console.WriteLine("PUSH TYPE " + Json.Str(p?["data"] ?? p, "PushType"));
    push.Start();
    await Task.Delay(15000);
    return;
}

if (args.Contains("--signals"))
{
    await using var ws = new StompClient(api, api.BaseUri.Host);
    ws.Status += Console.WriteLine;
    ws.Message += (type, _) => Console.WriteLine("STOMP TYPE " + type);
    ws.Start();
    var doors = new List<Door>();
    foreach (var p in await api.PlacesAsync())
        doors.AddRange(await api.DoorsAsync(p.Id));
    foreach (var d in doors.Where(d => d.Type == "SIP"))
    {
        var cred = await api.SipAsync(d);
        await using var sip = new SipClient(cred, d);
        sip.Status += Console.WriteLine;
        sip.Incoming += c => Console.WriteLine("SIP INCOMING (identity omitted)");
        await sip.StartAsync();
        await Task.Delay(15000);
    }

    return;
}

if (args.Contains("--media"))
{
    LibVLCSharp.Shared.Core.Initialize();
    using var vlc = new LibVLCSharp.Shared.LibVLC("--vout=dummy", "--aout=dummy");
    using var player = new LibVLCSharp.Shared.MediaPlayer(vlc);
    var placesForMedia = await api.PlacesAsync();
    var mediaDoors = await api.DoorsAsync(placesForMedia[0].Id);
    var door = mediaDoors.First(d => d.CameraId is not null);
    foreach (var ts in new long? []
    {
        null,
        DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()
    }

    )
    {
        var url = await api.VideoAsync(door.CameraId!, ts, door.TimeZone);
        using var media = new LibVLCSharp.Shared.Media(vlc, new Uri(url));
        player.Play(media);
        await Task.Delay(7000);
        var stats = media.Statistics;
        Console.WriteLine($"{(ts is null ? "LIVE" : "ARCHIVE")} VLC read={stats.ReadBytes} video={stats.DecodedVideo} audio={stats.DecodedAudio} playing={player.IsPlaying}");
        player.Stop();
    }

    try
    {
        var snapshot = await api.SnapshotAsync(door);
        Console.WriteLine("SNAPSHOT bytes=" + snapshot.Length + " jpeg=" + (snapshot.Length > 2 && snapshot[0] == 255 && snapshot[1] == 216));
    }
    catch (Exception e)
    {
        Console.WriteLine("SNAPSHOT " + e.Message);
    }

    var raw = await api.CameraEventsAsync(door.CameraId!, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
    Console.WriteLine("CAMERA EVENTS count=" + Json.Array(raw).Count);
    if (Json.Array(raw).FirstOrDefault()is JsonObject example)
        Console.WriteLine("CAMERA EVENT KEYS " + string.Join(',', example.Select(x => x.Key)));
    return;
}

try
{
    var boot = await api.BootstrapAsync();
    Console.WriteLine("BOOTSTRAP " + string.Join(",", (boot as JsonObject ?? new()).Select(x => x.Key)));
    Console.WriteLine("DOMAINS " + boot?["MOBILE_URL"]?.ToJsonString());
    Console.WriteLine("AUTH PROVIDER " + boot?["AUTH_PROVIDER"]?.ToJsonString());
}
catch (Exception e)
{
    Console.WriteLine("BOOTSTRAP " + e.Message);
}

var places = await api.PlacesAsync();
Console.WriteLine("PLACES " + places.Count);
foreach (var place in places)
{
    var doors = await api.DoorsAsync(place.Id);
    Console.WriteLine("DOORS " + doors.Count);
    foreach (var door in doors)
        Console.WriteLine("DEVICE " + new JsonObject { ["type"] = door.Type, ["openMethod"] = door.OpenMethod, ["allowOpen"] = door.AllowOpen, ["allowVideo"] = door.AllowVideo, ["cameraPresent"] = door.CameraId is not null, ["keys"] = new JsonArray(door.Raw.Select(x => (JsonNode? )JsonValue.Create(x.Key)).ToArray()) }.ToJsonString());
    try
    {
        var events = await api.EventsAsync(place.Id);
        Console.WriteLine("EVENTS " + events.Count);
        if (events.Count > 0)
            Console.WriteLine("EVENT KEYS " + string.Join(',', events[0].Raw.Select(x => x.Key)));
    }
    catch (Exception e)
    {
        Console.WriteLine("EVENTS " + e.Message);
    }

    foreach (var door in doors.Where(d => d.AllowVideo && d.CameraId is not null).Take(1))
    {
        try
        {
            var url = await api.VideoAsync(door.CameraId!);
            using var h = new HttpClient();
            using var r = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine($"LIVE HTTP {(int)r.StatusCode} / {r.Content.Headers.ContentType?.MediaType} host {new Uri(url).Host}");
        }
        catch (Exception e)
        {
            Console.WriteLine("LIVE " + e.Message);
        }

        try
        {
            var events = await api.CameraEventsAsync(door.CameraId!, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
            var d = Json.Data(events);
            Console.WriteLine("CAMERA EVENTS " + (d is JsonObject o ? string.Join(',', o.Select(x => x.Key)) : d?.GetType().Name));
        }
        catch (Exception e)
        {
            Console.WriteLine("CAMERA EVENTS " + e.Message);
        }

        try
        {
            var url = await api.VideoAsync(door.CameraId!, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), door.TimeZone);
            using var h = new HttpClient();
            using var r = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine($"ARCHIVE HTTP {(int)r.StatusCode} / {r.Content.Headers.ContentType?.MediaType}");
        }
        catch (Exception e)
        {
            Console.WriteLine("ARCHIVE " + e.Message);
        }
    }
}








