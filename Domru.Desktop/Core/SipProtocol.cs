using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Domru.Desktop.Core;
public sealed record SipMessage(string FirstLine, List<KeyValuePair<string, string>> Headers, string Body)
{
    private static string Normalize(string s) => s.ToLowerInvariant() switch
    {
        "v" => "via",
        "f" => "from",
        "t" => "to",
        "i" => "call-id",
        "m" => "contact",
        "l" => "content-length",
        var k => k
    };
    public string Get(string key) => Headers.FirstOrDefault(h => Normalize(h.Key) == key.ToLowerInvariant()).Value ?? "";
    public IEnumerable<string> All(string key) => Headers.Where(h => Normalize(h.Key) == key).Select(h => h.Value);
    public static SipMessage Parse(string raw)
    {
        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? raw : raw[..split];
        var lines = head.Split("\r\n");
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0)
                headers.Add(new(line[..i].Trim(), line[(i + 1)..].Trim()));
        }

        return new(lines[0], headers, split < 0 ? "" : raw[(split + 4)..]);
    }

    public string Response(int status, string reason, string tag, string? sdp = null, string? contact = null)
    {
        var lines = new List<string>
        {
            $"SIP/2.0 {status} {reason}"};
        foreach (var h in Headers)
        {
            var key = Normalize(h.Key);
            if (key is not ("via" or "from" or "to" or "call-id" or "cseq" or "record-route"))
                continue;
            var v = h.Value;
            if (key == "to" && status != 100 && !v.Contains(";tag="))
                v += ";tag=" + tag;
            lines.Add(h.Key + ": " + v);
        }

        if (contact is not null)
            lines.Add("Contact: " + contact);
        lines.Add("User-Agent: Myhome/Myhome-android");
        if (sdp is not null)
            lines.Add("Content-Type: application/sdp");
        lines.Add("Content-Length: " + Encoding.UTF8.GetByteCount(sdp ?? ""));
        return string.Join("\r\n", lines) + "\r\n\r\n" + (sdp ?? "");
    }
}

public static class SipProtocol
{
    public static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    public static Dictionary<string, string> Challenge(string text) => Regex.Matches(text, "([\\w-]+)\\s*=\\s*(?:\"([^\"]*)\"|([^,\\s]+))").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, StringComparer.OrdinalIgnoreCase);
    public static string Digest(string login, string password, string realm, string nonce, string method, string uri, string? qop = null, string? opaque = null, string? cnonce = null, string nc = "00000001")
    {
        cnonce ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var ha1 = Md5(login + ":" + realm + ":" + password);
        var ha2 = Md5(method + ":" + uri);
        var auth = qop?.Split(',').Select(x => x.Trim()).Contains("auth") == true;
        var response = auth ? Md5($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}") : Md5($"{ha1}:{nonce}:{ha2}");
        var result = $"Digest username=\"{login}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{response}\", algorithm=MD5";
        if (auth)
            result += $", qop=auth, nc={nc}, cnonce=\"{cnonce}\"";
        if (opaque is not null)
            result += ", opaque=\"" + opaque + "\"";
        return result;
    }

    public static IPEndPoint? ParseStun(byte[] b)
    {
        if (b.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(b) != 0x0101 || BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)) != 0x2112a442)
            return null;
        for (int i = 20; i + 4 <= b.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i));
            var len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
            i += 4;
            if (i + len > b.Length)
                return null;
            if (type is 0x0020 or 0x0001 && len >= 8 && b[i + 1] == 1)
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
                var ip = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i + 4));
                if (type == 0x0020)
                {
                    port ^= 0x2112;
                    ip ^= 0x2112a442;
                }

                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, ip);
                return new(new IPAddress(bytes), port);
            }

            i += len + (4 - len % 4) % 4;
        }

        return null;
    }

    public static byte[] StunRequest()
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(b, 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 0x2112a442);
        RandomNumberGenerator.Fill(b.AsSpan(8));
        return b;
    }

    public static string UriFrom(string value)
    {
        var m = Regex.Match(value, "<([^>]+)>");
        return m.Success ? m.Groups[1].Value : value.Split(';')[0];
    }

    public static (IPEndPoint Remote, int Payload, string Codec) AudioOffer(string sdp)
    {
        var lines = sdp.Split('\n').Select(x => x.Trim()).ToArray();
        var index = Array.FindIndex(lines, l => l.StartsWith("m=audio "));
        if (index < 0)
            throw new InvalidOperationException("Нет звука в SIP SDP");
        var media = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (media.Length < 4 || media[2] != "RTP/AVP")
            throw new InvalidOperationException("Домофон предлагает неподдерживаемый RTP-профиль");
        var audio = lines.Skip(index).TakeWhile((l, i) => i == 0 || !l.StartsWith("m=")).ToArray();
        var conn = audio.FirstOrDefault(l => l.StartsWith("c=IN IP4 ")) ?? lines.Take(index).LastOrDefault(l => l.StartsWith("c=IN IP4 "));
        if (conn is null || !IPAddress.TryParse(conn[9..].Split('/')[0], out var ip))
            throw new InvalidOperationException("Нет RTP адреса");
        foreach (var p in media.Skip(3))
        {
            if (!int.TryParse(p, out var pt))
                continue;
            var map = audio.FirstOrDefault(l => l.StartsWith($"a=rtpmap:{pt} "));
            var codec = map?.Split(' ')[1].ToUpperInvariant() ?? (pt == 0 ? "PCMU/8000" : pt == 8 ? "PCMA/8000" : "");
            if (codec is "PCMA/8000" or "PCMU/8000")
                return (new IPEndPoint(ip, int.Parse(media[1].Split('/')[0])), pt, codec);
        }

        throw new InvalidOperationException("Нет совместимого G.711 кодека");
    }
}
