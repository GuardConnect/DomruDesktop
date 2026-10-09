using System.Text.Json.Nodes;

namespace Domru.Desktop.Core;
public record AuthSession(string AccessToken, string? RefreshToken, string? OperatorId);
public record Place(string Id, string Name, JsonObject Raw)
{
    public override string ToString() => Name;
}

public record Door(string Id, string PlaceId, string Name, string Type, string? CameraId, string? ExternalDeviceId, string OpenMethod, bool AllowOpen, bool AllowVideo, long TimeZone, JsonObject Raw)
{
    public override string ToString() => Name;
}

public record HistoryEvent(string Id, string Title, DateTimeOffset? Time, string? CameraId, JsonObject Raw);
public record SipCredentials(string Login, string Password, string Realm);
public static class Json
{
    public static JsonNode? Data(JsonNode? n) => n is JsonObject o && o.ContainsKey("data") ? o["data"] : n;
    public static string? Str(JsonNode? n, params string[] keys)
    {
        if (n is not JsonObject o)
            return null;
        foreach (var k in keys)
            if (o[k] is JsonValue v)
                return v.ToString();
        return null;
    }

    public static bool Bool(JsonNode? n, string key, bool fallback = true) => bool.TryParse(Str(n, key), out var v) ? v : fallback;
    public static JsonArray Array(JsonNode? n)
    {
        n = Data(n);
        return n as JsonArray ?? n?["items"] as JsonArray ?? n?["events"] as JsonArray ?? n?["content"] as JsonArray ?? new();
    }

    public static DateTimeOffset? Time(JsonNode? n)
    {
        if (n is null)
            return null;
        if (long.TryParse(n.ToString(), out var t))
        {
            try
            {
                return t > 100000000000 ? DateTimeOffset.FromUnixTimeMilliseconds(t) : DateTimeOffset.FromUnixTimeSeconds(t);
            }
            catch
            {
                return null;
            }
        }

        return DateTimeOffset.TryParse(n.ToString(), out var d) ? d : null;
    }
}
