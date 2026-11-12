using System.Buffers.Binary;
using System.Text;

namespace ReSekai;

sealed class Binary(byte[] bytes, bool big = false, int offset = 0, int? length = null)
{
    public int Position { get; set; } = offset;
    public bool Big { get; set; } = big;
    readonly int end = checked(offset + (length ?? bytes.Length - offset));
    public int Remaining => end - Position;

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new InvalidDataException($"读取越界：{Position} + {count} > {end}");
        var part = bytes.AsSpan(Position, count);
        Position += count;
        return part;
    }

    public byte Byte() => Take(1)[0];
    public ushort U16() => Big ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => Big ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong U64() => Big ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public int Int() => unchecked((int)U32());
    public long Long() => unchecked((long)U64());
    public void Align(int alignment = 4) => Take((alignment - Position % alignment) % alignment);
    public int Count(int minimum = 1)
    {
        int count = Int();
        if (count < 0 || (long)count * minimum > Remaining) throw new InvalidDataException($"无效长度：{count}");
        return count;
    }
    public string Text()
    {
        int count = bytes.AsSpan(Position, Remaining).IndexOf((byte)0);
        if (count < 0) throw new InvalidDataException("字符串缺少终止符");
        string text = Encoding.UTF8.GetString(Take(count));
        Byte();
        return text;
    }
}
