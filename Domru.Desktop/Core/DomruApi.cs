using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Domru.Desktop.Core;
public sealed class DomruApi : IDisposable
{
    public const string DefaultBase = "https://myhome.proptech.ru/";
    private const string AuthSecret = "789sdgHJs678wertv34712376";
    private readonly HttpClient http;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    public Uri BaseUri { get; private set; } = new(DefaultBase);
    public string InstallationId { get; }
    public AuthSession? Session { get; private set; }

    public event Action<AuthSession?>? SessionChanged;
    public DomruApi(string installationId, AuthSession? session = null, HttpMessageHandler? handler = null)
    {
        InstallationId = installationId;
        Session = session;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string UserAgent => $"Google sdk_gphone64_x86_64 | Android 14 | erth | 9.11.0 (91100000) | | {Session?.OperatorId ?? "null"} | {InstallationId} | null";

    public static JsonObject PasswordBody(string login, string password, DateTimeOffset now) => new()
    {
        ["login"] = login,
        ["timestamp"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["hash1"] = Convert.ToBase64String(SHA1.HashData(Encoding.Latin1.GetBytes(password))),
        ["hash2"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("DigitalHomeNTKpassword" + login + password + now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + AuthSecret)))
    };
    public async Task<JsonNode?> RequestAsync(string path, HttpMethod? method = null, JsonNode? body = null, bool authenticated = true, Dictionary<string, string>? extra = null, bool retry = true, CancellationToken ct = default)
    {
        var oldToken = Session?.AccessToken;
        using var req = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(BaseUri, path));
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (authenticated && Session is not null)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Session.AccessToken);
        if (Session?.OperatorId is not null)
            req.Headers.TryAddWithoutValidation("Operator", Session.OperatorId);
        if (extra is not null)
            foreach (var(k, v)in extra)
            {
                req.Headers.Remove(k);
                req.Headers.TryAddWithoutValidation(k, v);
            }

        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await http.SendAsync(req, ct);
        if (r.StatusCode == HttpStatusCode.Unauthorized && authenticated && retry && Session?.RefreshToken is not null)
        {
            await RefreshAsync(oldToken, ct);
            return await RequestAsync(path, method, body, authenticated, extra, false, ct);
        }

        var text = await r.Content.ReadAsStringAsync(ct);
        JsonNode? result;
        try
        {
            result = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
        }
        catch
        {
            throw new InvalidOperationException($"API: HTTP {(int)r.StatusCode}, некорректный JSON");
        }

        if (!r.IsSuccessStatusCode && r.StatusCode != HttpStatusCode.MultipleChoices)
        {
            var message = Json.Str(result, "message", "error_description", "error") ?? "запрос отклонён";
            throw new InvalidOperationException($"API: HTTP {(int)r.StatusCode} — {message[..Math.Min(180, message.Length)]}");
        }

        return result;
    }

    private void SetSession(JsonNode? result)
    {
        var d = Json.Data(result);
        var token = Json.Str(d, "accessToken", "access_token") ?? throw new InvalidOperationException("В ответе нет токена доступа");
        Session = new(token, Json.Str(d, "refreshToken", "refresh_token") ?? Session?.RefreshToken, Json.Str(d, "operatorId", "operator_id") ?? Session?.OperatorId);
        SessionChanged?.Invoke(Session);
    }

    public async Task PasswordLoginAsync(string login, string password) => SetSession(await RequestAsync($"auth/v2/auth/{Uri.EscapeDataString(login)}/password", HttpMethod.Post, PasswordBody(login, password, DateTimeOffset.UtcNow), false));
    private static Dictionary<string, string> Basic(string phone) => new()
    {
        {
            "Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(phone + ":" + AuthSecret))
        }
    };
    public Task<JsonNode?> GetPhoneAccountsAsync(string phone) => RequestAsync($"auth/v2/login/{Uri.EscapeDataString(phone)}", authenticated: false, extra: Basic(phone));
    public async Task SendSmsAsync(string phone, JsonObject account) => _ = await RequestAsync($"auth/v2/confirmation/{Uri.EscapeDataString(phone)}", HttpMethod.Post, account, false, Basic(phone));
    public async Task ConfirmSmsAsync(string phone, string code, JsonObject account)
    {
        var body = (JsonObject)account.DeepClone();
        body["login"] = phone;
        body["confirm1"] = code;
        body["confirm2"] = code;
        if (body["subscriberId"] is not null)
            body["subscriberId"] = body["subscriberId"]!.ToString();
        SetSession(await RequestAsync($"auth/v3/auth/{Uri.EscapeDataString(phone)}/confirmation", HttpMethod.Post, body, false, Basic(phone)));
    }

    public async Task RefreshAsync(string? previousToken = null, CancellationToken ct = default)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            if (previousToken is not null && Session?.AccessToken != previousToken)
                return;
            var token = Session?.RefreshToken ?? throw new InvalidOperationException("Войдите в аккаунт заново");
            SetSession(await RequestAsync("auth/v2/session/refresh", authenticated: false, extra: new() { { "Bearer", token } }, ct: ct));
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public void Logout()
    {
        Session = null;
        SessionChanged?.Invoke(null);
    }

    public async Task<JsonNode?> BootstrapAsync()
    {
        var d = Json.Data(await RequestAsync("api/mh-customer-device/mobile/public/v1/customers/device-installations", HttpMethod.Post, new JsonObject { ["appVersionCode"] = 91100000, ["installationId"] = InstallationId, ["appId"] = 4, ["appVersion"] = "9.11.0", ["platform"] = "google", ["isDevelop"] = false, ["deviceManufacturer"] = "Google", ["deviceModelName"] = "sdk_gphone64_x86_64", ["osVersion"] = "14", ["deviceId"] = InstallationId.Replace("-", "")[..16] }));
        var domain = d?["MOBILE_URL"]?["domain"];
        var backend = Json.Str(domain, "backend");
        if (backend is not null && Uri.TryCreate(backend.Contains("://") ? backend : "https://" + backend, UriKind.Absolute, out var url) && url.Scheme == "https" && (url.Host.EndsWith(".proptech.ru") || url.Host.EndsWith(".domru.ru")))
            BaseUri = new(url.GetLeftPart(UriPartial.Authority) + "/");
        return d;
    }

    public async Task<List<Place>> PlacesAsync() => Json.Array(await RequestAsync("rest/v3/subscriber-places")).OfType<JsonObject>().Select(x =>
    {
        var p = x["place"] ?? x;
        return new Place(Json.Str(p, "id") ?? "", Json.Str(p["address"], "visibleAddress") ?? Json.Str(p, "name") ?? "Адрес", x);
    }).ToList();
    public async Task<List<Door>> DoorsAsync(string place) => Json.Array(await RequestAsync($"rest/v1/places/{E(place)}/accesscontrols")).OfType<JsonObject>().Select(x => new Door(Json.Str(x, "id") ?? "", place, Json.Str(x, "name") ?? "Домофон", Json.Str(x, "type") ?? "SIP", Json.Str(x, "externalCameraId"), Json.Str(x, "externalDeviceId"), Json.Str(x, "openMethod") ?? "SIP", Json.Bool(x, "allowOpen"), Json.Bool(x, "allowVideo"), long.TryParse(Json.Str(x, "timeZone"), out var tz) ? tz : 10800, x)).ToList();
    public async Task<List<HistoryEvent>> EventsAsync(string place) => ParseEvents(await RequestAsync($"rest/v1/places/{E(place)}/events?allowExtentedActions=true"));
    public Task<JsonNode?> AccessKeysAsync(string place) => RequestAsync($"api/mh-access-key/mobile/v1/rest/v1/access-keys?placeId={E(place)}");
    public Task<JsonNode?> FinanceAsync(string place) => RequestAsync($"api/mh-payment/mobile/v1/finance?placeId={E(place)}");
    public Task<JsonNode?> PaymentInfoAsync(string place) => RequestAsync($"api/mh-payment/mobile/v1/payment/info?placeId={E(place)}");
    public Task<JsonNode?> ProfileAsync() => RequestAsync("rest/v1/subscribers/profiles");
    public async Task<(List<HistoryEvent> Events, bool Last)> SearchEventsAsync(string place, int page = 0, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct=default)
    {
        if (!long.TryParse(place, out var id))
            throw new InvalidOperationException("Некорректный адрес");
        var body = new JsonObject
        {
            ["placeIds"] = new JsonArray(JsonValue.Create(id)),
            ["occurredAtFrom"] = from?.ToUnixTimeSeconds().ToString(),
            ["occurredAtTo"] = to?.ToUnixTimeSeconds().ToString(),
            ["sources"] = null,
            ["eventTypes"] = null
        };
        var result = await RequestAsync($"rest/v1/events/search?page={page}&sort=occurredAt%2CDESC", HttpMethod.Post, body, ct:ct);
        return (ParseEvents(result), Json.Bool(result, "last"));
    }

    public static List<HistoryEvent> ParseEvents(JsonNode? n) => Json.Array(n).OfType<JsonObject>().Select(x => new HistoryEvent(Json.Str(x, "id", "eventId", "ID") ?? "", Json.Str(x, "message", "Message", "eventTypeName", "name", "type") ?? (x.ContainsKey("Time") ? "Запись камеры" : "Событие"), Json.Time(x["timestamp"] ?? x["date"] ?? x["time"] ?? x["Date"] ?? x["Start"] ?? x["Time"]), Json.Str(x["source"], "externalCameraId", "cameraId") ?? Json.Str(x, "CameraID", "cameraId"), x)).ToList();
    public Task<JsonNode?> CameraEventsAsync(string camera, DateTimeOffset from, DateTimeOffset to,CancellationToken ct=default) => RequestAsync($"rest/v2/forpost/cameras/{E(camera)}/events?LowerDate={E(from.ToString("O"))}&UpperDate={E(to.ToString("O"))}&Count=200&orderByTime=DESC",ct:ct);
    public async Task<string> VideoAsync(string camera, long? timestamp = null, long timezone = 10800)
    {
        await RequestAsync($"api/mh-camera-personal/mobile/v1/video/refresh-user-session?externalCameraId={E(camera)}", HttpMethod.Put);
        // Android requests H264: a continuous HTTP-FLV response, not segmented HLS.
        // Archive playback retains HLS for its timeline and pause behavior.
        var path = $"rest/v1/forpost/cameras/{E(camera)}/video?LightStream=0" +
            (timestamp is null ? "&Format=H264" : $"&Format=HLS&TS={timestamp}&TZ={timezone}");
        var d = Json.Data(await RequestAsync(path));
        if (Json.Str(d, "Error")is { Length: > 0 } error)
            throw new InvalidOperationException(error);
        var url = Json.Str(d, "URL", "url") ?? throw new InvalidOperationException("Сервер не вернул видеопоток");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "rtsp" or "rtsps"))
            throw new InvalidOperationException("Недопустимый видеопоток");
        return url;
    }

    public async Task OpenAsync(Door door, string? entrance = null)
    {
        if (!door.AllowOpen)
            throw new InvalidOperationException("Открытие недоступно для этого домофона");
        if (door.OpenMethod == "FORPOST")
        {
            if (door.CameraId is null || door.ExternalDeviceId is null)
                throw new InvalidOperationException("Нет данных FORPOST");
            await RequestAsync($"rest/v1/forpost/cameras/{E(door.CameraId)}/devices/{E(door.ExternalDeviceId)}/open", HttpMethod.Post, extra: new() { { "X-Payment-PlaceId", door.PlaceId } });
        }
        else
            await RequestAsync($"rest/v1/places/{E(door.PlaceId)}/accesscontrols/{E(door.Id)}" + (entrance is null ? "" : $"/entrances/{E(entrance)}") + "/actions", HttpMethod.Post, new JsonObject { ["name"] = "accessControlOpen" });
    }

    public async Task<byte[]> SnapshotAsync(Door d)
    {
        var path = d.Type == "BUP" ? $"rest/v1/forpost/cameras/{E(d.CameraId ?? "")}/snapshots?width=1920&height=1080" : $"rest/v1/places/{E(d.PlaceId)}/accesscontrols/{E(d.Id)}/videosnapshots";
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, path));
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Session?.AccessToken);
        req.Headers.TryAddWithoutValidation("Operator", Session?.OperatorId);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var r = await http.SendAsync(req);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsByteArrayAsync();
    }

    public async Task<SipCredentials> SipAsync(Door d)
    {
        var r = Json.Data(await RequestAsync($"rest/v1/places/{E(d.PlaceId)}/accesscontrols/{E(d.Id)}/sipdevices", HttpMethod.Post, new JsonObject { ["installationId"] = InstallationId }));
        return new(Json.Str(r, "login") ?? throw new InvalidOperationException("Нет SIP login"), Json.Str(r, "password") ?? throw new InvalidOperationException("Нет SIP password"), Json.Str(r, "realm") ?? throw new InvalidOperationException("Нет SIP realm"));
    }

    public async Task RegisterPushAsync(string token)
    {
        var body = new JsonObject
        {
            ["appVersionCode"] = 91100000,
            ["installationId"] = InstallationId,
            ["appId"] = 4,
            ["appVersion"] = "9.11.0",
            ["platform"] = "google",
            ["isDevelop"] = false,
            ["deviceManufacturer"] = "Google",
            ["deviceModelName"] = "sdk_gphone64_x86_64",
            ["osVersion"] = "14",
            ["deviceId"] = InstallationId.Replace("-", "")[..16],
            ["pushToken"] = token
        };
        await RequestAsync("api/mh-customer-device/mobile/public/v1/customers/device-installations", HttpMethod.Post, body, authenticated: false);
        body["deviceType"] = "MOBILE_APPLICATION";
        await RequestAsync("rest/v1/subscriberNotifications", HttpMethod.Post, body);
    }

    public void Dispose()
    {
        http.Dispose();
        refreshLock.Dispose();
    }

    private static string E(string s) => Uri.EscapeDataString(s);
}

