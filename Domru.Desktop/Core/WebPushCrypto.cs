using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Domru.Desktop.Core;
public static class WebPushCrypto
{
    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string s)
    {
        s = s.Trim().Trim('"').Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }

    public static string Parameter(string header, string name)
    {
        foreach (var p in header.Split(';', ','))
        {
            var i = p.IndexOf('=');
            if (i > 0 && p[..i].Trim() == name)
                return p[(i + 1)..].Trim().Trim('"');
        }

        throw new InvalidDataException("Нет параметра Web Push " + name);
    }

    public static byte[] Public(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        return[4, ..p.Q.X!, ..p.Q.Y!];
    }

    private static byte[] Expand(byte[] prk, byte[] info, int length) => HMACSHA256.HashData(prk, (byte[])[..info, 1])[..length];
    public static byte[] Decrypt(byte[] encrypted, byte[] privateKey, byte[] secret, string cryptoHeader, string saltHeader, string encoding = "aesgcm")
    {
        using var mine = ECDiffieHellman.Create();
        mine.ImportPkcs8PrivateKey(privateKey, out _);
        byte[] other, salt;
        var modern = encoding == "aes128gcm";
        int recordSize = 4096;
        if (modern)
        {
            if (encrypted.Length < 21)
                throw new InvalidDataException("Обрезанный Web Push");
            salt = encrypted[..16];
            recordSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(16)));
            var len = encrypted[20];
            if (len != 65 || encrypted.Length < 21 + len)
                throw new InvalidDataException("Некорректный Web Push ключ");
            other = encrypted.AsSpan(21, len).ToArray();
            encrypted = encrypted[(21 + len)..];
        }
        else
        {
            other = Decode(Parameter(cryptoHeader, "dh"));
            salt = Decode(Parameter(saltHeader, "salt"));
        }

        if (other.Length != 65 || other[0] != 4)
            throw new InvalidDataException("Некорректная EC точка");
        using var theirs = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = other[1..33], Y = other[33..65] } });
        var shared = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        var authPrk = HMACSHA256.HashData(secret, shared);
        var own = Public(mine);
        byte[] info;
        if (modern)
            info = [..Encoding.ASCII.GetBytes("WebPush: info\0"), ..own, ..other];
        else
            info = Encoding.ASCII.GetBytes("Content-Encoding: auth\0");
        var ikm = Expand(authPrk, info, 32);
        var prk = HMACSHA256.HashData(salt, ikm);
        byte[] context = modern ? [] : [..Encoding.ASCII.GetBytes("P-256\0"), 0, 65, ..own, 0, 65, ..other];
        var key = Expand(prk, [..Encoding.ASCII.GetBytes(modern ? "Content-Encoding: aes128gcm\0" : "Content-Encoding: aesgcm\0"), ..context], 16);
        var nonce = Expand(prk, [..Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"), ..context], 12);
        if (recordSize < 18 || recordSize > 1024 * 1024)
            throw new InvalidDataException("Некорректный Web Push размер записи");
        using var output = new MemoryStream();
        using var aes = new AesGcm(key, 16);
        ulong sequence = 0;
        var blockSize = modern ? recordSize : recordSize + 16;
        for (int offset = 0; offset < encrypted.Length; offset += blockSize)
        {
            var block = encrypted.AsSpan(offset, Math.Min(blockSize, encrypted.Length - offset));
            if (block.Length < 16)
                throw new InvalidDataException("Обрезанная Web Push запись");
            var iv = (byte[])nonce.Clone();
            var counter = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(counter, sequence++);
            for (int i = 0; i < 8; i++)
                iv[4 + i] ^= counter[i];
            var plain = new byte[block.Length - 16];
            aes.Decrypt(iv, block[..^16], block[^16..], plain);
            if (modern)
            {
                var last = plain.Length - 1;
                while (last >= 0 && plain[last] == 0)
                    last--;
                if (last < 0 || plain[last] is not (1 or 2))
                    throw new InvalidDataException("Некорректный Web Push delimiter");
                output.Write(plain, 0, last);
            }
            else
            {
                if (plain.Length < 2)
                    throw new InvalidDataException();
                var padding = BinaryPrimitives.ReadUInt16BigEndian(plain);
                if (padding + 2 > plain.Length)
                    throw new InvalidDataException();
                output.Write(plain, 2 + padding, plain.Length - 2 - padding);
            }
        }

        return output.ToArray();
    }
}
