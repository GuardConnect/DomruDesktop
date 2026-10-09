using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domru.Desktop.Core;
public sealed record PushCredentials(ulong AndroidId, ulong SecurityToken, string AppId, string Token, string PrivateKey, string Secret, List<string> PersistentIds);
// Standalone .NET receiver: Google checkin -> Web Push registration -> TLS MCS.
public sealed class FcmReceiver : IAsyncDisposable
{
    // Public client configuration distributed with the Android APK; no user credentials.
    // Firebase API keys identify an app/project and are not backend authorization secrets:
    // https://firebase.google.com/docs/projects/api-keys
    // WebPushPublicKey below decodes to a 65-byte uncompressed P-256 PUBLIC point (04 || X || Y).
    // It is not an FCM server authentication key. Runtime tokens, PrivateKey and Secret
    // are freshly provisioned on the user's machine and persisted only in local DPAPI storage.
    private const string Project = "myhome-3b9cc", AppId = "1:1024394110794:android:aeead8f4e9e57a23", ApiKey = "AIzaSyDzAFxlGvy2IUkW896Kxpq3CZ-SqlUBy9s";
    private const string WebPushPublicKey = "BDOU99-h67HcA6JeFXHbSNMu7e2yNNu3RzoMj8TM4W88jITfq7ZmPvIM1Iv-4_l2LxQcYwhqby2xGpWwzjfAnG4";
    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly SessionStore store;
    private PushCredentials? credentials;
    private Task? run;
    private int streamId;
    public string Token => credentials?.Token ?? "";

    public event Action<string>? Status;
    public event Action<JsonNode?>? Message;
    public FcmReceiver(SessionStore store)
    {
        this.store = store;
        try
        {
            if (store.LoadPush()is { } data)
                credentials = JsonSerializer.Deserialize<PushCredentials>(data);
        }
        catch (JsonException)
        {
        }
    }

    public async Task PrepareAsync(CancellationToken ct = default)
    {
        if (credentials is not null)
            return;
        Status?.Invoke("FCM: регистрация устройства");
        var chrome = new Proto().Number(1, 3).Text(2, "144.0.7559.132").Number(3, 1).Build();
        var checkin = new Proto().Number(12, 3).Bytes(13, chrome).Build();
        var request = new Proto().Bytes(4, checkin).Number(14, 3).Number(22, 0).Build();
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new("application/x-protobuf");
        using var r = await http.PostAsync("https://android.clients.google.com/checkin", content, ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException("FCM checkin: HTTP " + (int)r.StatusCode);
        var fields = Proto.Read(await r.Content.ReadAsByteArrayAsync(ct));
        var android = fields.FirstOrDefault(x => x.Id == 7)?.Number ?? 0;
        var security = fields.FirstOrDefault(x => x.Id == 8)?.Number ?? 0;
        if (android == 0 || security == 0)
            throw new InvalidOperationException("FCM checkin не вернул идентификаторы");
        var subtype = "wp:com.ertelecom.smarthome#" + Guid.NewGuid();
        string? gcm = null;
        string registrationError = "";
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var reg = new HttpRequestMessage(HttpMethod.Post, "https://android.clients.google.com/c2dm/register3");
            reg.Headers.TryAddWithoutValidation("Authorization", $"AidLogin {android}:{security}");
            reg.Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "app", "org.chromium.linux" }, { "X-subtype", subtype }, { "device", android.ToString() }, { "sender", WebPushPublicKey }, { "gmsv", "144" }, { "scope", "GCM" }, { "X-scope", "GCM" } });
            using var rr = await http.SendAsync(reg, ct);
            var text = await rr.Content.ReadAsStringAsync(ct);
            if (rr.IsSuccessStatusCode && text.StartsWith("token="))
            {
                gcm = text[6..].Trim();
                break;
            }

            registrationError = $"HTTP {(int)rr.StatusCode} / " + (text.StartsWith("Error=") ? text[6..].Trim() : "неизвестный ответ");
            await Task.Delay(2000, ct);
        }

        if (gcm is null)
            throw new InvalidOperationException("FCM register3: " + registrationError);
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var secret = RandomNumberGenerator.GetBytes(16);
        var fid = RandomNumberGenerator.GetBytes(17);
        fid[0] = (byte)((fid[0] & 15) | 112);
        var install = await GoogleAsync($"https://firebaseinstallations.googleapis.com/v1/projects/{Project}/installations", new JsonObject { ["appId"] = AppId, ["authVersion"] = "FIS_v2", ["fid"] = WebPushCrypto.B64(fid)[..22], ["sdkVersion"] = "w:0.6.6" }, null, ct);
        var auth = Json.Str(install?["authToken"], "token") ?? throw new InvalidOperationException("FCM installation не вернул токен");
        var register = await GoogleAsync($"https://fcmregistrations.googleapis.com/v1/projects/{Project}/registrations", new JsonObject { ["web"] = new JsonObject { ["applicationPubKey"] = null, ["auth"] = WebPushCrypto.B64(secret), ["endpoint"] = "https://fcm.googleapis.com/fcm/send/" + gcm, ["p256dh"] = WebPushCrypto.B64(WebPushCrypto.Public(key)) } }, auth, ct);
        var token = Json.Str(register, "token") ?? throw new InvalidOperationException("FCM registration не вернул токен");
        credentials = new(android, security, subtype, token, WebPushCrypto.B64(key.ExportPkcs8PrivateKey()), WebPushCrypto.B64(secret), []);
        Persist();
        Status?.Invoke("FCM: устройство зарегистрировано");
    }

    private async Task<JsonNode?> GoogleAsync(string url, JsonObject body, string? auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey);
        if (auth is not null)
            req.Headers.TryAddWithoutValidation("x-goog-firebase-installations-auth", auth);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException("FCM " + new Uri(url).Host + ": HTTP " + (int)r.StatusCode);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    private void Persist()
    {
        if (credentials is not null)
            store.SavePush(JsonSerializer.SerializeToUtf8Bytes(credentials));
    }

    public void Start()
    {
        if (credentials is null)
            throw new InvalidOperationException("FCM не зарегистрирован");
        run = RunAsync(stop.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = 5;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(15000);
                    await tcp.ConnectAsync("mtalk.google.com", 5228, timeout.Token);
                }

                using var tls = new SslStream(tcp.GetStream(), false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "mtalk.google.com" }, ct);
                var c = credentials!;
                var login = new Proto().Text(1, "144.0.7559.132").Text(2, "mcs.android.com").Text(3, c.AndroidId.ToString()).Text(4, c.AndroidId.ToString()).Text(5, c.SecurityToken.ToString()).Text(6, "android-" + c.AndroidId.ToString("x")).Bytes(8, new Proto().Text(1, "new_vc").Text(2, "1").Build()).Number(12, 0).Number(14, 1).Number(16, 2).Number(17, 1);
                foreach (var id in c.PersistentIds.TakeLast(100))
                    login.Text(10, id);
                streamId = 0;
                await SendAsync(tls, 2, login.Build(), ct, true);
                var version = await ByteAsync(tls, ct);
                if (version != 41)
                    throw new InvalidDataException("Неизвестная версия FCM MCS");
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeat = HeartbeatAsync(tls, connection.Token);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                        var tag = await ByteAsync(tls, readTimeout.Token);
                        var length = await LengthAsync(tls, readTimeout.Token);
                        if (length > 1024 * 1024)
                            throw new InvalidDataException("Слишком большой FCM пакет");
                        var bytes = new byte[length];
                        await tls.ReadExactlyAsync(bytes, readTimeout.Token);
                        var fields = Proto.Read(bytes);
                        streamId++;
                        if (tag == 3)
                        {
                            if (fields.Any(x => x.Id == 3))
                                throw new InvalidOperationException("FCM MCS отклонил вход");
                            backoff = 5;
                            Status?.Invoke("FCM: подключено");
                        }
                        else if (tag == 0)
                            await SendAsync(tls, 1, new Proto().Number(2, (ulong)streamId).Build(), ct);
                        else if (tag is 4 or 10)
                            throw new IOException("FCM MCS разорвал соединение");
                        else if (tag == 8)
                        {
                            var id = fields.FirstOrDefault(x => x.Id == 9)?.Text;
                            var duplicate = id is not null && c.PersistentIds.Contains(id);
                            if (!duplicate)
                            {
                                try
                                {
                                    HandleData(fields);
                                }
                                catch (Exception e)when (e is CryptographicException or InvalidDataException or JsonException)
                                {
                                    Status?.Invoke("FCM: сообщение не удалось расшифровать");
                                }

                                if (id is not null)
                                {
                                    c.PersistentIds.Add(id);
                                    if (c.PersistentIds.Count > 100)
                                        c.PersistentIds.RemoveAt(0);
                                    Persist();
                                }
                            }

                            if (id is not null)
                            {
                                var selective = new Proto().Text(1, id).Build();
                                var iq = new Proto().Number(2, 1).Text(3, "").Bytes(7, new Proto().Number(1, 12).Bytes(2, selective).Build()).Number(10, (ulong)streamId).Build();
                                await SendAsync(tls, 7, iq, ct);
                            }
                        }
                    }
                }
                finally
                {
                    connection.Cancel();
                    try
                    {
                        await heartbeat;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            catch (OperationCanceledException)when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                Status?.Invoke($"FCM: переподключение через {backoff} с");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = Math.Min(backoff * 2, 300);
        }
    }

    private void HandleData(List<Proto.Field> fields)
    {
        var app = new Dictionary<string, string>();
        foreach (var f in fields.Where(x => x.Id == 7))
        {
            var p = Proto.Read(f.Bytes);
            var k = p.FirstOrDefault(x => x.Id == 1)?.Text;
            var v = p.FirstOrDefault(x => x.Id == 2)?.Text;
            if (k is not null && v is not null)
                app[k] = v;
        }

        if (app.GetValueOrDefault("message_type") == "deleted_messages")
            return;
        var raw = fields.FirstOrDefault(x => x.Id == 21)?.Bytes;
        if (raw is { Length: > 0 })
        {
            var c = credentials!;
            if (app.TryGetValue("subtype", out var subtype) && subtype != c.AppId)
                return;
            var plain = WebPushCrypto.Decrypt(raw, WebPushCrypto.Decode(c.PrivateKey), WebPushCrypto.Decode(c.Secret), app.GetValueOrDefault("crypto-key", ""), app.GetValueOrDefault("encryption", ""), app.GetValueOrDefault("content-encoding", "aesgcm"));
            Message?.Invoke(JsonNode.Parse(plain));
        }
        else if (app.ContainsKey("PushType"))
        {
            var n = new JsonObject();
            foreach (var(k, v)in app)
                n[k] = v;
            Message?.Invoke(new JsonObject { ["data"] = n });
        }
    }

    private async Task HeartbeatAsync(SslStream tls, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct))
            await SendAsync(tls, 0, new Proto().Number(2, (ulong)streamId).Build(), ct);
    }

    private async Task SendAsync(SslStream tls, byte tag, byte[] data, CancellationToken ct, bool version = false)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            using var packet = new MemoryStream();
            if (version)
                packet.WriteByte(41);
            packet.WriteByte(tag);
            Proto.Varint(packet, (ulong)data.Length);
            packet.Write(data);
            await tls.WriteAsync(packet.ToArray(), ct);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task<byte> ByteAsync(Stream s, CancellationToken ct)
    {
        var b = new byte[1];
        await s.ReadExactlyAsync(b, ct);
        return b[0];
    }

    private static async Task<int> LengthAsync(Stream s, CancellationToken ct)
    {
        uint result = 0;
        for (int i = 0; i < 5; i++)
        {
            var b = await ByteAsync(s, ct);
            result |= (uint)(b & 127) << (i * 7);
            if ((b & 128) == 0)
                return checked((int)result);
        }

        throw new InvalidDataException("Некорректная длина FCM");
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (run is not null)
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }

        stop.Dispose();
        http.Dispose();
        writeLock.Dispose();
    }
}
