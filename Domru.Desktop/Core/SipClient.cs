using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Domru.Desktop.Core;
public sealed record IncomingCall(string Id, Door Door, string Caller);
public sealed class SipClient : IAsyncDisposable
{
    private readonly SipCredentials credentials;
    public Door Door { get; }

    private readonly UdpClient udp = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    internal Func<RtpAudio> AudioFactory { get; set; } = () => new();
    internal IPEndPoint? ServerOverride { get; set; }
    internal bool EnableStun { get; set; } = true;

    private Task? run, register;
    private IPEndPoint? server;
    private IPAddress local = IPAddress.Loopback;
    private IPEndPoint? publicSip;
    private readonly string regCall = Guid.NewGuid().ToString(), regTag = Token();
    private string tag = Token(), regAuth = "", authHeader = "Authorization";
    private Dictionary<string, string>? digestChallenge;
    private string digestCnonce = Token();
    private uint nonceCount;
    private int regSequence = 1, authFailures;
    private SipMessage? invite;
    private IPEndPoint? peer;
    private RtpAudio? audio;
    private string? answer;
    private bool acknowledged;
    private string? dialogId;
    private int dialogSeq = 1;
    private CancellationTokenSource? callTimeout;
    private int expires = 60;
    private bool pushDriven;
    private string? pushToken, pushCallId;
    public bool Registered { get; private set; }
    public bool IsActive => audio is not null;
    public AudioTransmissionStats? AudioStats=>audio?.Stats;
    public bool ConversationConfirmed=>audio is not null&&acknowledged;
    public IncomingCall? CurrentCall { get; private set; }
    public int InputDevice { get; set; } = -1;
    public int OutputDevice { get; set; } = -1;

    public event Action<string>? Status;
    public event Action<IncomingCall>? Incoming;
    public event Action<string>? Ended;
    public SipClient(SipCredentials credentials, Door door)
    {
        this.credentials = credentials;
        Door = door;
    }

    private string Contact => $"<sip:{credentials.Login}@{publicSip!.Address}:{publicSip.Port};transport=udp" + (pushToken is null ? "" : $";app-id=erth;pn-type=google;pn-tok={pushToken}" + (pushCallId is null ? "" : ";Call-Id:%20" + Uri.EscapeDataString(pushCallId))) + ">";

    public async Task StartAsync(CancellationToken ct = default, bool onDemand = false, string? fcmToken = null)
    {
        if (credentials.Realm.IndexOfAny(['\r', '\n', ' ']) >= 0 || credentials.Login.IndexOfAny(['\r', '\n', ' ', '"']) >= 0)
            throw new InvalidOperationException("Некорректные SIP данные");
        if (ServerOverride is not null)
            server = ServerOverride;
        else
        {
            var addresses = await Dns.GetHostAddressesAsync(credentials.Realm, ct);
            server = new(addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork), 5060);
        }

        using (var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            route.Connect(server);
            local = ((IPEndPoint)route.LocalEndPoint!).Address;
        }

        var stable = 20000 + (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(Door.PlaceId + ":" + Door.Id))) % 30000);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, stable));
        publicSip = new(local, stable);
        if (EnableStun)
            try
            {
                var stunIp = (await Dns.GetHostAddressesAsync("stun.l.google.com", ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                var request = SipProtocol.StunRequest();
                await udp.SendAsync(request, new IPEndPoint(stunIp, 19302), ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(2000);
                var r = await udp.ReceiveAsync(timeout.Token);
                if (r.RemoteEndPoint.Address.Equals(stunIp) && r.Buffer.AsSpan(8, 12).SequenceEqual(request.AsSpan(8, 12)))
                    publicSip = SipProtocol.ParseStun(r.Buffer) ?? publicSip;
            }
            catch (Exception e)when (e is OperationCanceledException or SocketException)
            {
                Status?.Invoke("SIP: STUN недоступен");
            }

        pushDriven = onDemand;
        pushToken = fcmToken;
        run = ReceiveAsync(stop.Token);
        if (!onDemand)
        {
            register = RegisterLoopAsync(stop.Token);
            await RegisterAsync();
        }
        else
            Status?.Invoke("SIP: ожидание push-звонка");
    }

    public async Task RegisterOnRingAsync(string callId)
    {
        await gate.WaitAsync();
        try
        {
            pushCallId = callId;
            expires = 30;
            authFailures = 0;
            regAuth = "";
            digestChallenge = null;
            nonceCount = 0;
            await RegisterAsync();
            _ = ExpireOnDemandAsync(callId, stop.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExpireOnDemandAsync(string callId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(32000, ct);
            await gate.WaitAsync(ct);
            try
            {
                if (pushDriven && pushCallId == callId && invite is null)
                {
                    expires = 0;
                    await RegisterAsync();
                    Registered = false;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Token() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private Task Send(string message, IPEndPoint target) => udp.SendAsync(Encoding.UTF8.GetBytes(message), target, stop.Token).AsTask();
    private string Via => $"SIP/2.0/UDP {publicSip!.Address}:{publicSip.Port};branch=z9hG4bK{Token()};rport";

    private async Task RegisterAsync()
    {
        if (server is null)
            return;
        if (digestChallenge is { } ch)
        {
            nonceCount++;
            regAuth = SipProtocol.Digest(credentials.Login, credentials.Password, ch.GetValueOrDefault("realm", credentials.Realm), ch["nonce"], "REGISTER", "sip:" + credentials.Realm, ch.GetValueOrDefault("qop"), ch.GetValueOrDefault("opaque"), digestCnonce, nonceCount.ToString("x8"));
        }

        var headers = new List<string>
        {
            $"REGISTER sip:{credentials.Realm} SIP/2.0",
            "Via: " + Via,
            "Max-Forwards: 70",
            $"From: <sip:{credentials.Login}@{credentials.Realm}>;tag={regTag}",
            $"To: <sip:{credentials.Login}@{credentials.Realm}>",
            "Call-ID: " + regCall,
            $"CSeq: {regSequence++} REGISTER",
            "Contact: " + Contact,
            $"Expires: {expires}",
            "User-Agent: Myhome/Myhome-android",
            "Accept: application/sdp",
            "Supported: replaces, outbound, gruu, path"
        };
        if (regAuth.Length > 0)
            headers.Add(authHeader + ": " + regAuth);
        headers.Add("Content-Length: 0");
        await Send(string.Join("\r\n", headers) + "\r\n\r\n", server);
    }

    private async Task RegisterLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Registered ? Math.Max(10, expires - 10) * 1000 : 5000, ct);
                await RegisterAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
            Status?.Invoke("SIP: сеть недоступна");
        }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(ct);
                if (server is null || !r.RemoteEndPoint.Address.Equals(server.Address) || r.Buffer.Length > 65507)
                    continue;
                var raw = Encoding.UTF8.GetString(r.Buffer);
                if (!raw.Contains("SIP/2.0"))
                    continue;
                var m = SipMessage.Parse(raw);
                await gate.WaitAsync(ct);
                try
                {
                    await Handle(m, r.RemoteEndPoint);
                }
                catch (Exception e)when (e is not OperationCanceledException)
                {
                    Status?.Invoke("SIP: " + e.Message);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
            if (!ct.IsCancellationRequested)
                Status?.Invoke("SIP: соединение потеряно");
        }
    }

    private async Task Handle(SipMessage m, IPEndPoint source)
    {
        if (m.FirstLine.StartsWith("SIP/2.0"))
        {
            var code = int.TryParse(m.FirstLine.Split(' ').ElementAtOrDefault(1), out var n) ? n : 0;
            if (m.Get("call-id") == regCall && m.Get("cseq").EndsWith("REGISTER"))
            {
                if (code is 401 or 407)
                {
                    Registered = false;
                    if (++authFailures > 3)
                    {
                        Status?.Invoke("SIP: регистрация отклонена");
                        return;
                    }

                    var ch = SipProtocol.Challenge(m.Get(code == 401 ? "www-authenticate" : "proxy-authenticate"));
                    if (ch.GetValueOrDefault("algorithm", "MD5") != "MD5")
                        throw new InvalidOperationException("Неизвестный SIP Digest алгоритм");
                    if (digestChallenge?.GetValueOrDefault("nonce") != ch.GetValueOrDefault("nonce"))
                    {
                        nonceCount = 0;
                        digestCnonce = Token();
                    }

                    digestChallenge = ch;
                    authHeader = code == 401 ? "Authorization" : "Proxy-Authorization";
                    await RegisterAsync();
                }
                else if (code == 200)
                {
                    Registered = expires > 0;
                    authFailures = 0;
                    var exp = m.Get("expires");
                    if (int.TryParse(exp, out var seconds) && seconds > 0)
                        expires = Math.Clamp(seconds, 20, 300);
                    Status?.Invoke(expires == 0 ? "SIP: ожидание push-звонка" : "SIP: зарегистрирован");
                }
                else if (code >= 400)
                {
                    Registered = false;
                    Status?.Invoke($"SIP: регистрация HTTP/SIP {code}");
                }
            }

            return;
        }

        var method = m.FirstLine.Split(' ')[0];
        var id = m.Get("call-id");
        if (method is "OPTIONS" or "NOTIFY")
        {
            await Send(m.Response(200, "OK", tag, contact: Contact), source);
            return;
        }

        if (method == "INVITE")
        {
            if (invite is not null)
            {
                if (id == dialogId)
                {
                    await Send(answer ?? m.Response(180, "Ringing", tag), source);
                }
                else
                    await Send(m.Response(486, "Busy Here", Token()), source);
                return;
            }

            invite = m;
            peer = source;
            tag = Token();
            dialogId = id;
            acknowledged = false;
            answer = null;
            await Send(m.Response(100, "Trying", tag), source);
            await Send(m.Response(180, "Ringing", tag, contact: Contact), source);
            CurrentCall = new(id, Door, SipProtocol.UriFrom(m.Get("from")));
            Incoming?.Invoke(CurrentCall);
            callTimeout = new CancellationTokenSource();
            _ = TimeoutAsync(id, callTimeout.Token);
        }
        else if (method == "CANCEL")
        {
            await Send(m.Response(200, "OK", tag), source);
            if (id == dialogId && invite is not null && audio is null)
            {
                await Send(invite.Response(487, "Request Terminated", tag), peer!);
                await EndCall("Звонок отменён");
            }
        }
        else if (method == "ACK" && id == dialogId)
        {
            acknowledged = true;
            Status?.Invoke("SIP: разговор подключён");
        }
        else if (method == "BYE")
        {
            if (id != dialogId)
            {
                await Send(m.Response(481, "Call Does Not Exist", Token()), source);
                return;
            }

            await Send(m.Response(200, "OK", tag), source);
            await EndCall("Звонок завершён");
        }
    }

    private async Task TimeoutAsync(string id, CancellationToken ct)
    {
        try
        {
            await Task.Delay(35000, ct);
            await gate.WaitAsync(ct);
            try
            {
                if (dialogId == id && audio is null)
                {
                    if (invite is not null)
                        await Send(invite.Response(480, "Temporarily Unavailable", tag), peer!);
                    await EndCall("Пропущенный звонок");
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task AnswerAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (invite is null || peer is null)
                throw new InvalidOperationException("Входящий звонок уже закончился");
            if (audio is not null)
                return;
            var offer = SipProtocol.AudioOffer(invite.Body);
            var a = AudioFactory();
            a.InputDevice = InputDevice;
            a.OutputDevice = OutputDevice;
            a.Status += s => Status?.Invoke(s);
            try
            {
                await a.PrepareAsync(local, stop.Token);
                a.Start(offer.Remote, offer.Payload, offer.Codec);
                var endpoint = a.PublicEndpoint;
                var sdp = $"v=0\r\no=- {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} 1 IN IP4 {endpoint.Address}\r\ns=DomruDesktop\r\nc=IN IP4 {endpoint.Address}\r\nt=0 0\r\nm=audio {endpoint.Port} RTP/AVP {offer.Payload}\r\na=rtpmap:{offer.Payload} {offer.Codec}\r\na=sendrecv\r\na=ptime:20\r\n";
                answer = invite.Response(200, "OK", tag, sdp, Contact);
                audio = a;
                callTimeout?.Cancel();
                await Send(answer, peer);
                _ = RetransmitAnswerAsync(dialogId!, stop.Token);
            }
            catch
            {
                await a.DisposeAsync();
                audio = null;
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RetransmitAnswerAsync(string id, CancellationToken ct)
    {
        try
        {
            for (int i = 0; i < 7; i++)
            {
                await Task.Delay(Math.Min(4000, 500 << i), ct);
                if (id != dialogId || acknowledged || answer is null || peer is null)
                    return;
                await Send(answer, peer);
            }

            if (id == dialogId && !acknowledged)
                await HangupAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void SetMute(bool value)
    {
        if (audio is not null)
            audio.Muted = value;
    }

    public async Task HangupAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (invite is null || peer is null)
                return;
            if (audio is null)
                await Send(invite.Response(486, "Busy Here", tag), peer);
            else
            {
                var target = SipProtocol.UriFrom(invite.Get("contact"));
                if (string.IsNullOrEmpty(target))
                    target = SipProtocol.UriFrom(invite.Get("from"));
                var localTo = invite.Get("to");
                if (!localTo.Contains(";tag="))
                    localTo += ";tag=" + tag;
                var lines = new List<string>
                {
                    $"BYE {target} SIP/2.0",
                    "Via: " + Via,
                    "Max-Forwards: 70",
                    "From: " + localTo,
                    "To: " + invite.Get("from"),
                    "Call-ID: " + dialogId,
                    $"CSeq: {dialogSeq++} BYE"};
                foreach (var route in invite.All("record-route"))
                    lines.Add("Route: " + route);
                lines.Add("Content-Length: 0");
                await Send(string.Join("\r\n", lines) + "\r\n\r\n", peer);
            }

            await EndCall("Звонок завершён");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EndCall(string reason)
    {
        callTimeout?.Cancel();
        callTimeout?.Dispose();
        callTimeout = null;
        var old = audio;
        audio = null;
        invite = null;
        answer = null;
        CurrentCall = null;
        dialogId = null;
        peer = null;
        if (old is not null)
            await old.DisposeAsync();
        if (pushDriven)
        {
            expires = 0;
            await RegisterAsync();
            Registered = false;
        }

        Ended?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await HangupAsync();
            if (server is not null)
            {
                expires = 0;
                await RegisterAsync();
            }
        }
        catch (Exception e)when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        stop.Cancel();
        udp.Dispose();
        foreach (var t in new[]
        {
            run,
            register
        }

        )
            if (t is not null)
                try
                {
                    await t;
                }
                catch (OperationCanceledException)
                {
                }

        stop.Dispose();
        gate.Dispose();
    }
}

