using System.Buffers.Binary;
using System.Text;

namespace Domru.Desktop.Core;
// Small protobuf wire codec for the public Chromium checkin/MCS message schemas.
public sealed class Proto
{
    private readonly MemoryStream stream = new();
    public static void Varint(Stream s, ulong n)
    {
        while (n >= 128)
        {
            s.WriteByte((byte)(n | 128));
            n >>= 7;
        }

        s.WriteByte((byte)n);
    }

    public Proto Number(int field, ulong value)
    {
        Varint(stream, (ulong)(field << 3));
        Varint(stream, value);
        return this;
    }

    public Proto Text(int field, string text) => Bytes(field, Encoding.UTF8.GetBytes(text));
    public Proto Bytes(int field, byte[] bytes)
    {
        Varint(stream, (ulong)((field << 3) | 2));
        Varint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
        return this;
    }

    public byte[] Build() => stream.ToArray();
    public record Field(int Id, int Wire, ulong Number, byte[] Bytes)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }

    public static List<Field> Read(byte[] bytes)
    {
        var fields = new List<Field>();
        var i = 0;
        ulong V()
        {
            ulong n = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (i >= bytes.Length)
                    throw new InvalidDataException("Обрезанный protobuf");
                var b = bytes[i++];
                n |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0)
                    return n;
            }

            throw new InvalidDataException("Некорректный protobuf varint");
        }

        while (i < bytes.Length)
        {
            var key = V();
            var id = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (id == 0)
                throw new InvalidDataException("Некорректный protobuf field");
            ulong n = 0;
            byte[] value = [];
            switch (wire)
            {
                case 0:
                    n = V();
                    break;
                case 1:
                    if (i + 8 > bytes.Length)
                        throw new InvalidDataException();
                    n = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i));
                    i += 8;
                    break;
                case 2:
                    var size = checked((int)V());
                    if (size < 0 || i + size > bytes.Length)
                        throw new InvalidDataException();
                    value = bytes.AsSpan(i, size).ToArray();
                    i += size;
                    break;
                case 5:
                    if (i + 4 > bytes.Length)
                        throw new InvalidDataException();
                    n = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
                    i += 4;
                    break;
                default:
                    throw new InvalidDataException("Неизвестный protobuf wire type");
            }

            fields.Add(new(id, wire, n, value));
        }

        return fields;
    }
}
