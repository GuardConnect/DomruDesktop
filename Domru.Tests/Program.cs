using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Domru.Desktop.Core;
using NAudio.Codecs;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    passed++;
}

Check(EventCaption.CameraDayDescription(new("test","--- 23:56:39 - 23:57:09",null,null,new()))=="с 23:56:39 по 23:57:09","Camera day strips malformed separators and formats time range");
Check(EventCaption.CameraDayDescription(new("test","--- 23:56:39 — 23:57:09",null,null,new()))=="с 23:56:39 по 23:57:09","Camera day handles Unicode range separator");
var recordStart=new DateTimeOffset(DateTime.SpecifyKind(new DateTime(2026,10,9,23,59,48),DateTimeKind.Local));
Check(EventCaption.FullRecordingInterval(new("record","",recordStart,null,new(){["Duration"]=30}))=="с 09.10.2026 23:59:48 по 10.10.2026 00:00:18","Recording header shows full dates across midnight");

var stereoMic=new byte[12];
for(int frame=0;frame<3;frame++){BinaryPrimitives.WriteInt16LittleEndian(stereoMic.AsSpan(frame*4),2);BinaryPrimitives.WriteInt16LittleEndian(stereoMic.AsSpan(frame*4+2),(short)(frame==1?-6000:4000));}
var normalizedMic=AudioChecks.Normalize(stereoMic,2);
Check(normalizedMic.Peak==6000&&normalizedMic.Pcm.Length==6&&BinaryPrimitives.ReadInt16LittleEndian(normalizedMic.Pcm.AsSpan(2))==-6000,"Microphone check retains active right channel on stereo interface");
Check(AudioChecks.Normalize(new byte[16],2).Peak==0,"Microphone check reports real silence");
var monoMic=new byte[]{0x10,0x27,0xf0,0xd8};
Check(AudioChecks.Normalize(monoMic,1).Pcm.SequenceEqual(monoMic),"Microphone check preserves mono samples and polarity");
var quietVoice=new byte[4];BinaryPrimitives.WriteInt16LittleEndian(quietVoice,1000);BinaryPrimitives.WriteInt16LittleEndian(quietVoice.AsSpan(2),-1000);
var louderVoice=AudioChecks.PrepareVoicePlayback(quietVoice);
Check(BinaryPrimitives.ReadInt16LittleEndian(louderVoice)==8000&&BinaryPrimitives.ReadInt16LittleEndian(louderVoice.AsSpan(2))==-8000,"Recorded quiet voice is amplified for audible playback without changing polarity");
Check(AudioChecks.PrepareVoicePlayback(new byte[8]).All(x=>x==0),"Voice playback does not amplify digital silence");
using(var resource=typeof(AudioAssets).Assembly.GetManifestResourceStream("DomruDesktop.Audio.Ringtone.wav"))
{
    Check(resource is not null&&Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(resource))=="2af9a70592ecea20fb478dd39cc5ae2b356a7c567997cd3a791b118888b4f6e5","Embedded ringtone exactly matches supplied intercom WAV");
}
using(var ring=AudioAssets.OpenRingtone())Check(ring.WaveFormat.Channels==2&&ring.TotalTime.TotalSeconds>7,"Embedded ringtone opens directly from assembly resources");
using(var stereoReader=new NAudio.Wave.WaveFileReader(Path.Combine(Environment.CurrentDirectory,"Domru.Desktop","Assets","AudioTests","headphones.wav")))
{
    var data=new byte[checked((int)stereoReader.Length)];var offset=0;while(offset<data.Length){var count=stereoReader.Read(data,offset,data.Length-offset);if(count==0)break;offset+=count;}
    var firstLeft=-1;var firstRight=-1;var firstBoth=-1;
    for(int frame=0;frame<data.Length/4;frame++){var left=BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(frame*4));var right=BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(frame*4+2));if(left!=0&&right==0&&firstLeft<0)firstLeft=frame;if(right!=0&&left==0&&firstRight<0)firstRight=frame;if(left!=0&&right!=0&&firstBoth<0)firstBoth=frame;}
    Check(stereoReader.WaveFormat.Channels==2&&firstLeft>=0&&firstRight>firstLeft&&firstBoth>firstRight,"Stereo headphone test plays left, right, then both channels in order");
}

var body = DomruApi.PasswordBody("test-user", "correct horse", DateTimeOffset.Parse("2026-10-07T18:00:00Z"));
Check(body["hash1"]!.ToString() == "L55TUjtiq8FBorTWAZ0jy6g129A=", "Password SHA1 independent vector");
Check(body["hash2"]!.ToString() == "15f5b992bd720c2ee99dd5139705f0e6", "Password MD5 independent vector");
var digest = SipProtocol.Digest("Mufasa", "Circle Of Life", "testrealm@host.com", "dcd98b7102dd2f0e8b11d0f600bfb0c093", "GET", "/dir/index.html", "auth", cnonce: "0a4f113b");
Check(digest.Contains("6629fae49393a05397450978507c4ef1"), "RFC 2617 Digest vector");
var compact = SipMessage.Parse("INVITE sip:test SIP/2.0\r\nv: first\r\nv: second\r\nf: <sip:caller>;tag=remote\r\nt: <sip:local>\r\ni: test-call\r\nCSeq: 1 INVITE\r\nRecord-Route: <sip:route>\r\nl: 0\r\n\r\n");
var response = compact.Response(200, "OK", "local");
Check(compact.Get("call-id") == "test-call" && response.Contains("v: first") && response.Contains("v: second") && response.Contains("t: <sip:local>;tag=local") && response.Contains("Record-Route:"), "Compact SIP and multi-Via echo");
var sdp = "v=0\r\nc=IN IP4 127.0.0.1\r\nm=audio 40000 RTP/AVP 101 8 0\r\na=rtpmap:101 telephone-event/8000\r\na=rtpmap:8 PCMA/8000\r\n";
Check(SipProtocol.AudioOffer(sdp).Payload == 8, "SDP codec selection skips DTMF");
bool rejected = false;
try
{
    SipProtocol.AudioOffer(sdp.Replace("RTP/AVP", "RTP/SAVP"));
}
catch (InvalidOperationException)
{
    rejected = true;
}

Check(rejected, "Unsupported encrypted profile fails explicitly");
var eventJson = JsonNode.Parse("{\"data\":[{\"ID\":1,\"Time\":1791385200,\"Message\":\"Движение\",\"CameraID\":\"camera\"}]}");
var events = DomruApi.ParseEvents(eventJson);
Check(events[0].Title == "Движение" && events[0].Time?.ToUnixTimeSeconds() == 1791385200 && events[0].CameraId == "camera", "Archive event response casing");
var proto = new Proto().Number(1, 300).Text(3, "Привет").Bytes(7, [0, 255]).Build();
var fields = Proto.Read(proto);
Check(fields.First(x => x.Id == 1).Number == 300 && fields.First(x => x.Id == 3).Text == "Привет" && fields.First(x => x.Id == 7).Bytes.SequenceEqual(new byte[] { 0, 255 }), "Protobuf mixed wire fields");
foreach (var vector in JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "crypto-vectors.json")))!.AsArray())
{
    var decrypted = WebPushCrypto.Decrypt(WebPushCrypto.Decode(vector!["cipher"]!.ToString()), WebPushCrypto.Decode(vector["privateKey"]!.ToString()), WebPushCrypto.Decode(vector["secret"]!.ToString()), vector["cryptoHeader"]!.ToString(), vector["saltHeader"]!.ToString(), vector["encoding"]!.ToString());
    Check(Encoding.UTF8.GetString(decrypted) == vector["plain"]!.ToString(), "Independent Web Push " + vector["encoding"]);
}

var calls = new List<string>();
var handler = new FakeHttp(async (req) =>
{
    calls.Add(req.RequestUri!.AbsolutePath);
    if (req.RequestUri.AbsolutePath.EndsWith("refresh"))
        return Response("{\"accessToken\":\"new\",\"refreshToken\":\"next\",\"operatorId\":2}");
    if (req.Headers.Authorization?.Parameter == "old")
        return Response("{}", 401);
    if (req.RequestUri.AbsolutePath.Contains("actions"))
    {
        var content = await req.Content!.ReadAsStringAsync();
        Check(content.Contains("accessControlOpen"), "Door command payload");
        return Response("{}");
    }

    return Response("{\"data\":[]}");
});
using (var api = new DomruApi(Guid.NewGuid().ToString(), new("old", "refresh", "2"), handler))
{
    await api.PlacesAsync();
    Check(api.Session!.AccessToken == "new" && calls.Count(x => x.EndsWith("refresh")) == 1, "401 refresh and retry");
    var door = new Door("door", "place", "Test", "SIP", null, null, "ACCESS_CONTROL", true, false, 10800, new());
    await api.OpenAsync(door);
    Check(calls.Last() == "/rest/v1/places/place/accesscontrols/door/actions", "REST open correct target");
}

using (var api = new DomruApi(Guid.NewGuid().ToString(), new("old", "refresh", "2"), new FakeHttp(_ => Task.FromResult(Response("{}", 500)))))
{
    bool failed = false;
    try
    {
        await api.OpenAsync(new("d", "p", "Test", "SIP", null, null, "ACCESS_CONTROL", true, false, 10800, new()));
    }
    catch (InvalidOperationException)
    {
        failed = true;
    }

    Check(failed, "Server failure is not reported as successful door opening");
}

await TestSipAsync();
await TestPushRegistrationAsync();
var videoPaths=new List<string>();
using(var videoApi=new DomruApi(Guid.NewGuid().ToString(),new("test","test","2"),new FakeHttp(req=>
{
    videoPaths.Add(req.RequestUri!.PathAndQuery);
    return Task.FromResult(Response(req.Method==HttpMethod.Put?"{}":"{\"data\":{\"URL\":\"https://media.invalid/video\"}}"));
})))
{
    await videoApi.VideoAsync("camera");
    Check(videoPaths.Last().Contains("Format=H264")&&!videoPaths.Last().Contains("TS="),"Live mirrors Android H264 stream without HLS or archive timestamp");
    await videoApi.VideoAsync("camera",1791385200,10800);
    Check(videoPaths.Last().Contains("Format=HLS")&&videoPaths.Last().Contains("TS=1791385200")&&videoPaths.Last().Contains("TZ=10800"),"Archive retains HLS and requested timestamp");
}
await TestArchiveAsync();
await TestArchiveCameraAsync();
var captionStart=new DateTimeOffset(2026,10,8,13,38,18,TimeSpan.FromHours(3)).ToLocalTime();
var caption=new HistoryEvent("test","► 13:38:18 - 13:38:48",captionStart,null,new JsonObject{["Time"]=captionStart.ToUnixTimeSeconds(),["Duration"]=30});
Check(EventCaption.ArchiveTitle(caption)=="Запись камеры","Archive recording label excludes server range");
Check(EventCaption.ArchiveTime(caption)==captionStart.ToString("HH:mm:ss")+" — "+captionStart.AddSeconds(30).ToString("HH:mm:ss"),"Archive interval retains seconds without duplicate dates");
Check(EventCaption.Clean("<b>Дверь</b> &amp; звонок ► 12:00")=="Дверь & звонок — 12:00","Caption HTML and arrow rendered correctly");
Check(EventCaption.ArchiveTitle(new("test","Входящий звонок",captionStart,null,new()))=="Входящий звонок","Archive preserves event meaning");
var dayNow=DateTimeOffset.Parse("2026-10-08T14:00:00+03:00");
Check(ArchiveHistory.DayRange(dayNow,0)==(DateTimeOffset.Parse("2026-10-08T00:00:00+03:00"),dayNow),"Today archive stops at current time");
Check(ArchiveHistory.DayRange(dayNow,1)==(DateTimeOffset.Parse("2026-10-07T00:00:00+03:00"),DateTimeOffset.Parse("2026-10-07T23:59:59.999+03:00")),"Yesterday archive includes only its calendar date");
Check(ArchiveHistory.DayRange(dayNow,2).From==DateTimeOffset.Parse("2026-10-06T00:00:00+03:00"),"Day before yesterday has correct midnight boundary");
await TestArchiveDayAsync();
var keySample=AccountServices.ParseKeys(JsonNode.Parse("[{\"id\":1,\"accessKey\":{\"accessKeyCode\":\"test-code\",\"name\":\"Family\",\"state\":\"ACTIVE\",\"accessControls\":[{\"name\":\"Entrance\"}]}}]"));
Check(keySample.Count==1&&keySample[0].Code=="test-code"&&keySample[0].State=="Активен"&&keySample[0].Doors=="Entrance","APK nested access key fields parsed");
var orderedKeys=AccountServices.ParseKeys(JsonNode.Parse("[{\"id\":10,\"accessKey\":{\"name\":\"Ten\",\"accessKeyCode\":\"a\"}},{\"id\":2,\"accessKey\":{\"name\":\"Two\",\"accessKeyCode\":\"b\"}},{\"id\":1,\"accessKey\":{\"name\":\"One\",\"accessKeyCode\":\"c\"}},{\"accessKey\":{\"name\":\"Unknown\",\"accessKeyCode\":\"d\"}}]"));
Check(orderedKeys.Select(x=>x.Id).SequenceEqual(new string?[]{"1","2","10",null}),"Keys sorted by numeric server ID with missing IDs last");
Check(AccountServices.IsHttps("https://pay.cyfral-group.ru/?account=test")&&!AccountServices.IsHttps("javascript:alert(1)")&&!AccountServices.IsHttps("file:///C:/test"),"Payment links accept HTTPS browser URLs only");
Check(AccountServices.Provider("https://pay.cyfral-group.ru/")=="Цифрал-Сервис"&&AccountServices.Provider("https://cyfral-group.ru.fake.invalid")=="Дом.ру","Payment provider matches complete domain");
Check(AccountServices.Money(JsonNode.Parse("{\"balance\":0}"),"balance").StartsWith("0,00")&&AccountServices.Money(new JsonObject(),"balance")=="Нет данных","Finance distinguishes zero balance from unavailable data");
var midnightStart=new DateTimeOffset(new DateTime(2026,10,6,23,59,48));
var midnightRecord=new HistoryEvent("midnight","record",midnightStart,null,new(){["Duration"]=30});
Check(EventCaption.ArchiveTime(midnightRecord)=="23:59:48 — 00:00:18","Midnight recording time does not mix date into interval");
Check(EventCaption.ArchiveDates(midnightRecord)=="06.10.2026 — 07.10.2026","Midnight recording has separate complete date range");
Check(EventCaption.ArchiveDates(caption)=="","Same-day recording does not repeat header date");
var yearRecord=midnightRecord with{Time=new DateTimeOffset(new DateTime(2026,12,31,23,59,48))};
Check(EventCaption.ArchiveDates(yearRecord)=="31.12.2026 — 01.01.2027","Recording dates cross year correctly");
var settingsPath=Path.Combine(Environment.CurrentDirectory,"tools","autoplay-settings-tests",Guid.NewGuid().ToString("N"));
var settingsStore=new SessionStore(settingsPath);Check(settingsStore.LoadCameraAutoplay(),"Camera autoplay enabled by default");settingsStore.SaveCameraAutoplay(false);
Check(!new SessionStore(settingsPath).LoadCameraAutoplay(),"Camera autoplay disabled preference survives reload");settingsStore.SaveCameraAutoplay(true);
Check(new SessionStore(settingsPath).LoadCameraAutoplay(),"Camera autoplay enabled preference survives reload");
settingsStore.SaveAudioDevices("Mic example","Headphones example");
var audioDevices=new SessionStore(settingsPath).LoadAudioDevices();
Check(audioDevices.Microphone=="Mic example"&&audioDevices.Speaker=="Headphones example","Microphone and headphones choices survive application restart");
var actualStartup=new StartupRegistration();var initialStartup=actualStartup.IsEnabled();
var startupTestKey=@"Software\DomruDesktop\Diagnostics\"+Guid.NewGuid().ToString("N");
var startupTest=new StartupRegistration(startupTestKey,"TestStartup");
try
{
    Check(!startupTest.IsEnabled(),"Windows startup disabled without registration");
    var startupExe=Path.Combine(Environment.CurrentDirectory,"Example Folder","Домофон.exe");
    startupTest.SetEnabled(true,startupExe);Check(startupTest.IsEnabled(),"Startup registration enables in isolated diagnostic key");
    using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupTestKey))Check(key?.GetValue("TestStartup")?.ToString()=="\""+startupExe+"\" --tray","Startup command preserves spaces and launches into tray");
    startupTest.SetEnabled(false,startupExe);Check(!startupTest.IsEnabled(),"Disabling startup removes registration");
    Check(actualStartup.IsEnabled()==initialStartup,"Startup tests leave real Windows autorun unchanged");
}
finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(startupTestKey,false);}
Check(settingsStore.LoadCloseToTray(),"Close-to-tray retains previous default behavior");settingsStore.SaveCloseToTray(false);
Check(!new SessionStore(settingsPath).LoadCloseToTray(),"Normal-close preference survives reload");settingsStore.SaveCloseToTray(true);
Check(new SessionStore(settingsPath).LoadCloseToTray(),"Close-to-tray enabled preference survives reload");
Console.WriteLine($"RESULT: {passed} passed");
async Task TestArchiveDayAsync()
{
    var now=DateTimeOffset.Parse("2026-10-08T14:00:00+03:00");var range=ArchiveHistory.DayRange(now,1);bool correctBounds=false;
    using var api=new DomruApi(Guid.NewGuid().ToString(),new("test","test","2"),new FakeHttp(async req=>
    {
        var body=JsonNode.Parse(await req.Content!.ReadAsStringAsync());correctBounds=body?["occurredAtFrom"]?.ToString()==range.From.ToUnixTimeSeconds().ToString()&&body?["occurredAtTo"]?.ToString()==range.To.ToUnixTimeSeconds().ToString();
        JsonObject Row(string id,DateTimeOffset time)=>new(){["id"]=id,["timestamp"]=time.ToUnixTimeMilliseconds(),["message"]="event"};
        return Response(new JsonObject{["content"]=new JsonArray(Row("first",range.From),Row("last",range.To),Row("next",range.To.AddMilliseconds(1)),Row("previous",range.From.AddMilliseconds(-1))),["last"]=true}.ToJsonString());
    }));
    var result=await ArchiveHistory.LoadDayAsync(api,new("door","123","Test","SIP",null,null,"ACCESS_CONTROL",true,true,10800,new()),now,1);
    Check(correctBounds&&result.Select(x=>x.Id).SequenceEqual(new[]{"last","first"}),"Day query sends exact bounds and excludes neighbouring days");
}
async Task TestArchiveCameraAsync()
{
    var now=DateTimeOffset.Parse("2026-10-08T14:00:00+03:00");var calls=0;string? secondQuery=null;
    using var api=new DomruApi(Guid.NewGuid().ToString(),new("test","test","2"),new FakeHttp(req=>
    {
        if(req.Method==HttpMethod.Post)return Task.FromResult(Response("{\"content\":[],\"last\":true}"));
        var rows=new JsonArray();var index=calls++;
        if(index==0)for(int i=1;i<=200;i++)rows.Add(new JsonObject{["ID"]=i,["Time"]=now.AddMinutes(-i).ToUnixTimeSeconds(),["Message"]="Движение",["CameraID"]="camera"});
        else {secondQuery=Uri.UnescapeDataString(req.RequestUri!.Query);rows.Add(new JsonObject{["ID"]=201,["Time"]=ArchiveHistory.FourDayStart(now).AddHours(1).ToUnixTimeSeconds(),["Message"]="Движение",["CameraID"]="camera"});}
        return Task.FromResult(Response(rows.ToJsonString()));
    }));
    var result=await ArchiveHistory.LoadAsync(api,new("door","123","Test","SIP","camera",null,"ACCESS_CONTROL",true,true,10800,new()),now);
    Check(result.Count==201&&calls==2,"Four-day archive includes camera records beyond first 200");
    Check(secondQuery?.Contains(DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(-200).ToUnixTimeSeconds()).AddMilliseconds(-1).ToString("O"))==true,"Camera pagination moves upper bound before oldest loaded record");
}
async Task TestArchiveAsync()
{
    var now=DateTimeOffset.Parse("2026-10-08T14:00:00+03:00");var from=ArchiveHistory.FourDayStart(now);
    Check(from==DateTimeOffset.Parse("2026-10-05T00:00:00+03:00"),"Archive range covers today and previous three dates");
    JsonObject Event(string id,DateTimeOffset time,string source)=>new(){["id"]=id,["message"]=id,["timestamp"]=time.ToUnixTimeMilliseconds(),["source"]=new JsonObject{["id"]=source}};
    var requests=0;bool correctBounds=true;
    using var api=new DomruApi(Guid.NewGuid().ToString(),new("test","test","2"),new FakeHttp(async req=>
    {
        if(req.Method==HttpMethod.Get)return Response("[]");
        var body=JsonNode.Parse(await req.Content!.ReadAsStringAsync());correctBounds&=body?["occurredAtFrom"]?.ToString()==from.ToUnixTimeSeconds().ToString()&&body?["occurredAtTo"]?.ToString()==now.ToUnixTimeSeconds().ToString();
        var rows=new JsonArray();var page=requests++;
        if(page==0){rows.Add(Event("recent",now.AddHours(-1),"door"));rows.Add(Event("other",now.AddHours(-2),"other-door"));rows.Add(Event("old",from.AddSeconds(-1),"door"));rows.Add(Event("future",now.AddSeconds(1),"door"));}
        if(page==1){rows.Add(Event("recent",now.AddHours(-1),"door"));rows.Add(Event("boundary",from,"door"));rows.Add(Event("camera",now.AddDays(-1),"camera"));}
        return Response(new JsonObject{["content"]=rows,["last"]=page==2}.ToJsonString());
    }));
    var door=new Door("door","123","Test","SIP","camera",null,"ACCESS_CONTROL",true,true,10800,new());
    var result=await ArchiveHistory.LoadAsync(api,door,now);
    Check(requests==3&&correctBounds,"Archive fetches every page with fixed date bounds");
    Check(result.Select(x=>x.Id).SequenceEqual(new[]{"recent","camera","boundary"}),"Archive deduplicates and filters selected door, camera and date range");
    using var cancel=new CancellationTokenSource();cancel.Cancel();bool cancelled=false;try{await ArchiveHistory.LoadAsync(api,door,now,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&requests==3,"Archive cancellation prevents obsolete network requests");
}
async Task TestPushRegistrationAsync()
{
    var correct = 0;
    using (var api = new DomruApi(Guid.NewGuid().ToString(), new("access", "refresh", "2"), new FakeHttp(async req =>
    {
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync());
        if (body?["appId"]?.ToString() == "4" && body?["pushToken"]?.ToString() == "synthetic-token") correct++;
        return Response("{}");
    }))) await api.RegisterPushAsync("synthetic-token");
    Check(correct == 2, "ERTH appId=4 on both push registration endpoints");
    using var registrar = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var door = new Door(Guid.NewGuid().ToString(), "test", "Test", "SIP", null, null, "ACCESS_CONTROL", true, true, 10800, new());
    await using var sip = new SipClient(new("test", "password", "localhost"), door) { ServerOverride = (IPEndPoint)registrar.Client.LocalEndPoint!, EnableStun = false };
    await sip.StartAsync(onDemand: true, fcmToken: "synthetic-token");
    await sip.RegisterOnRingAsync("synthetic-call");
    var bytes = await registrar.ReceiveAsync(timeout.Token);
    var contact = SipMessage.Parse(Encoding.UTF8.GetString(bytes.Buffer)).Get("contact");
    Check(contact.Contains("app-id=erth") && contact.Contains("pn-tok=synthetic-token") && contact.Contains("Call-Id:%20synthetic-call"), "ERTH SIP push contact uses APK identifiers");
}
HttpResponseMessage Response(string json, int status = 200) => new((HttpStatusCode)status)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};
async Task TestSipAsync()
{
    using var registrar = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var rtpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    var door = new Door("test-" + Guid.NewGuid(), "local", "Test door", "SIP", null, null, "ACCESS_CONTROL", true, true, 10800, new());
    RtpAudio? audio = null;
    await using var client = new SipClient(new("test", "password", "localhost"), door)
    {
        ServerOverride = (IPEndPoint)registrar.Client.LocalEndPoint!,
        EnableStun = false,
        AudioFactory = () => audio = new RtpAudio
        {
            UseAudioDevices = false,
            EnableStun = false
        }
    };
    var incoming = new TaskCompletionSource<IncomingCall>(TaskCreationOptions.RunContinuationsAsynchronously);
    var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.Incoming += c => incoming.TrySetResult(c);
    client.Ended += s => ended.TrySetResult(s);
    await client.StartAsync();
    async Task<(SipMessage Message, IPEndPoint Endpoint)> Next()
    {
        var datagram = await registrar.ReceiveAsync(timeout.Token);
        return (SipMessage.Parse(Encoding.UTF8.GetString(datagram.Buffer)), datagram.RemoteEndPoint);
    }

    async Task Send(string text, IPEndPoint endpoint) => await registrar.SendAsync(Encoding.UTF8.GetBytes(text), endpoint, timeout.Token);
    var initial = await Next();
    await Send(initial.Message.Response(401, "Unauthorized", "registrar").Replace("Content-Length: 0", "WWW-Authenticate: Digest realm=\"localhost\", nonce=\"nonce\", qop=\"auth\"\r\nContent-Length: 0"), initial.Endpoint);
    var authenticated = await Next();
    Check(authenticated.Message.Get("authorization").Contains("qop=auth"), "SIP challenge roundtrip");
    await Send(authenticated.Message.Response(200, "OK", "registrar"), initial.Endpoint);
    await Task.Delay(50);
    Check(client.Registered, "SIP registration confirmed");
    var mediaPort = ((IPEndPoint)rtpServer.Client.LocalEndPoint!).Port;
    var invite = $"INVITE sip:test@localhost SIP/2.0\r\nv: SIP/2.0/UDP 127.0.0.1;branch=z9hG4bKtest\r\nf: <sip:000@localhost>;tag=remote\r\nt: <sip:test@localhost>\r\ni: local-call\r\nm: <sip:000@localhost>\r\nRecord-Route: <sip:127.0.0.1:{initial.Endpoint.Port};lr>\r\nCSeq: 42 INVITE\r\nContent-Type: application/sdp\r\n\r\nv=0\r\nc=IN IP4 127.0.0.1\r\nm=audio {mediaPort} RTP/AVP 8\r\na=rtpmap:8 PCMA/8000\r\n";
    await Send(invite, initial.Endpoint);
    await incoming.Task.WaitAsync(timeout.Token);
    Check((await Next()).Message.FirstLine.Contains("100") && (await Next()).Message.FirstLine.Contains("180"), "Incoming INVITE rings before answer");
    await client.AnswerAsync();
    var answer = await Next();
    Check(answer.Message.FirstLine.Contains("200") && answer.Message.Body.Contains("a=rtpmap:8 PCMA/8000"), "Answer negotiates G711 SDP");
    await Send("ACK sip:test@localhost SIP/2.0\r\ni: local-call\r\nCSeq: 42 ACK\r\n\r\n", initial.Endpoint);
    var uplink = await rtpServer.ReceiveAsync(timeout.Token);
    Check(uplink.Buffer.Length == 172 && (uplink.Buffer[1] & 127) == 8, "RTP uplink sends 20ms G711 packets");
    var microphoneTone=new byte[960*4];
    for(int frame=0;frame<960;frame++)BinaryPrimitives.WriteInt16LittleEndian(microphoneTone.AsSpan(frame*4+2),(short)(8000*Math.Sin(frame*2*Math.PI*400/48000)));
    for(int i=0;i<5;i++){audio!.SubmitCapturedPcm(microphoneTone,48000,2);await Task.Delay(20);}
    var voicePeak=0;
    for(int i=0;i<12;i++){var speech=await rtpServer.ReceiveAsync(timeout.Token);for(int p=12;p<speech.Buffer.Length;p++)voicePeak=Math.Max(voicePeak,Math.Abs((int)ALawDecoder.ALawToLinearSample(speech.Buffer[p])));}
    Check(voicePeak>3000&&client.AudioStats?.CapturedBytes>0&&client.AudioStats.SpeechPackets>0,"Answered SIP call carries captured right-channel microphone voice in G711 RTP");
    client.SetMute(true);var mutedPeak=0;
    for(int i=0;i<20;i++){var muted=await rtpServer.ReceiveAsync(timeout.Token);if(i>=10)for(int p=12;p<muted.Buffer.Length;p++)mutedPeak=Math.Max(mutedPeak,Math.Abs((int)ALawDecoder.ALawToLinearSample(muted.Buffer[p])));}
    Check(mutedPeak<=8,"Microphone mute stops voice in outgoing RTP");client.SetMute(false);
    var packet = new byte[172];
    packet[0] = 128;
    packet[1] = 8;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 1);
    Array.Fill(packet, ALawEncoder.LinearToALawSample(1000), 12, 160);
    await rtpServer.SendAsync(packet, uplink.RemoteEndPoint, timeout.Token);
    await Task.Delay(80);
    Check(audio!.ReceivedPackets == 1 && audio.LastPcm?.Length == 320, "RTP downlink decodes to PCM");
    await client.HangupAsync();
    var bye = await Next();
    Check(bye.Message.FirstLine.StartsWith("BYE sip:000@localhost") && bye.Message.Get("route").Length > 0 && bye.Message.Get("from").Contains(";tag="), "BYE preserves dialog and route");
    await ended.Task.WaitAsync(timeout.Token);
    Check(client.CurrentCall is null && !client.IsActive, "Call teardown releases RTP and state");
    // A cancelled ring must acknowledge CANCEL and terminate the INVITE transaction.
    incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await Send(invite.Replace("local-call", "cancel-call"), initial.Endpoint);
    await incoming.Task.WaitAsync(timeout.Token);
    await Next();
    await Next();
    await Send("CANCEL sip:test@localhost SIP/2.0\r\ni: cancel-call\r\nCSeq: 42 CANCEL\r\n\r\n", initial.Endpoint);
    var cancel = await Next();
    var terminated = await Next();
    Check(cancel.Message.FirstLine.Contains("200") && terminated.Message.FirstLine.Contains("487"), "CANCEL completes both SIP transactions");
}

sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}

