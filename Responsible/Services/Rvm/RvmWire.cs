using System.Text;

namespace Responsible.Services;

internal sealed class RvmProtocolException(string message) : Exception(message);

// Minimal protobuf (proto3) writer/reader for the RVM v1 wire format. Field numbers
// mirror RVM/protocol/rvm/v1/*.proto; unknown fields are preserved as opaque slices.
internal static class RvmProto
{
    public static void AddVarint(List<byte> target, int field, ulong value)
    {
        WriteTag(target, field, 0);
        WriteVarint(target, value);
    }

    public static void AddBool(List<byte> target, int field, bool value) => AddVarint(target, field, value ? 1UL : 0UL);

    public static void AddString(List<byte> target, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(target, field, 2);
        WriteVarint(target, (ulong)bytes.Length);
        target.AddRange(bytes);
    }

    public static void AddMessage(List<byte> target, int field, Action<List<byte>> body)
    {
        var nested = new List<byte>();
        body(nested);
        WriteTag(target, field, 2);
        WriteVarint(target, (ulong)nested.Count);
        target.AddRange(nested);
    }

    private static void WriteTag(List<byte> target, int field, int wire) =>
        WriteVarint(target, ((ulong)field << 3) | (uint)wire);

    public static void WriteVarint(List<byte> target, ulong value)
    {
        while (value >= 0x80)
        {
            target.Add((byte)(value | 0x80));
            value >>= 7;
        }
        target.Add((byte)value);
    }
}

internal sealed class RvmProtoReader
{
    private readonly byte[] _payload;
    private readonly Dictionary<int, List<Field>> _fields = [];

    private readonly record struct Field(int Wire, int Offset, int Length, ulong Varint);

    private RvmProtoReader(byte[] payload) => _payload = payload;

    public static RvmProtoReader Parse(byte[] payload)
    {
        var reader = new RvmProtoReader(payload);
        var offset = 0;
        while (offset < payload.Length)
        {
            var tag = ReadVarint(payload, ref offset);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (field <= 0) throw new RvmProtocolException("字段号非法");
            switch (wire)
            {
                case 0:
                {
                    var value = ReadVarint(payload, ref offset);
                    reader.Add(field, new Field(wire, offset, 0, value));
                    break;
                }
                case 1:
                    reader.Add(field, new Field(wire, offset, 8, 0));
                    offset += 8;
                    break;
                case 2:
                {
                    var length = ReadVarint(payload, ref offset);
                    if (length > int.MaxValue || offset + (int)length > payload.Length)
                        throw new RvmProtocolException("字段长度越界");
                    reader.Add(field, new Field(wire, offset, (int)length, 0));
                    offset += (int)length;
                    break;
                }
                case 5:
                    reader.Add(field, new Field(wire, offset, 4, 0));
                    offset += 4;
                    break;
                default:
                    throw new RvmProtocolException($"不支持的 wire type {wire}");
            }
            if (offset > payload.Length) throw new RvmProtocolException("消息被截断");
        }
        return reader;
    }

    private void Add(int field, Field value)
    {
        if (!_fields.TryGetValue(field, out var list))
            _fields[field] = list = [];
        list.Add(value);
    }

    private Field? Last(int field) =>
        _fields.TryGetValue(field, out var list) && list.Count > 0 ? list[^1] : null;

    public bool Has(int field) => _fields.ContainsKey(field);

    public ulong Varint(int field, ulong fallback = 0) => Last(field)?.Varint ?? fallback;

    public string String(int field)
    {
        var value = Last(field);
        if (value is not { Wire: 2 }) return string.Empty;
        return Encoding.UTF8.GetString(_payload, value.Value.Offset, value.Value.Length);
    }

    public RvmProtoReader? Message(int field)
    {
        var value = Last(field);
        if (value is not { Wire: 2 }) return null;
        return Parse(_payload[value.Value.Offset..(value.Value.Offset + value.Value.Length)]);
    }

    private static ulong ReadVarint(byte[] source, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= source.Length) throw new RvmProtocolException("varint 不完整");
            var current = source[offset++];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return result;
        }
        throw new RvmProtocolException("varint 过长");
    }
}