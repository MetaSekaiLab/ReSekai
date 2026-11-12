namespace ReSekai;

static class Bundle
{
    public static bool Matches(ReadOnlySpan<byte> head) => head.StartsWith("UnityFS\0"u8) || head.StartsWith(new byte[] { 16, 0, 0, 0 });

    public static List<(string Name, byte[] Bytes)> Read(byte[] input)
    {
        if (input.AsSpan().StartsWith(new byte[] { 16, 0, 0, 0 }))
        {
            input = input[4..];
            for (int i = 0; i < Math.Min(128, input.Length); i++) if (i % 8 < 5) input[i] ^= 255;
        }
        Binary header = new(input, true);
        if (header.Text() != "UnityFS") throw new InvalidDataException("不是 UnityFS 资源包");
        int version = header.Int();
        if (version is < 6 or > 8) throw new NotSupportedException($"UnityFS 版本 {version}");
        header.Text(); header.Text();
        if (header.Long() != input.Length) throw new InvalidDataException("资源包长度不符");
        int packed = checked((int)header.U32()), unpacked = checked((int)header.U32());
        uint flags = header.U32();
        if (version >= 7) header.Align(16);
        int information = (flags & 128) != 0 ? input.Length - packed : header.Position;
        Binary index = new(Decode(input.AsSpan(information, packed), unpacked, (int)(flags & 63)), true);
        int content = header.Position + ((flags & 128) == 0 ? packed : 0);
        if ((flags & 512) != 0) content = (content + 15) & ~15;
        index.Take(16);
        var blocks = new (int Plain, int Packed, int Codec)[index.Count(10)];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = (checked((int)index.U32()), checked((int)index.U32()), index.U16() & 63);
        byte[] data = new byte[checked(blocks.Sum(b => b.Plain))];
        int destination = 0;
        foreach (var block in blocks)
        {
            Decode(input.AsSpan(content, block.Packed), block.Plain, block.Codec).CopyTo(data, destination);
            content += block.Packed;
            destination += block.Plain;
        }
        int files = index.Count(21);
        List<(string, byte[])> result = [];
        for (int i = 0; i < files; i++)
        {
            int offset = checked((int)index.Long()), length = checked((int)index.Long());
            index.Int();
            string name = index.Text();
            result.Add((name, data.AsSpan(offset, length).ToArray()));
        }
        return result;
    }

    internal static byte[] Decode(ReadOnlySpan<byte> source, int size, int codec)
    {
        if (codec == 0)
        {
            if (source.Length != size) throw new InvalidDataException("未压缩块长度不符");
            return source.ToArray();
        }
        if (codec is not (2 or 3)) throw new NotSupportedException($"资源包压缩格式 {codec}");
        byte[] result = new byte[size];
        int read = 0, written = 0;
        while (read < source.Length)
        {
            byte token = source[read++];
            int literals = token >> 4;
            if (literals == 15)
            {
                byte more;
                do { more = source[read++]; literals = checked(literals + more); } while (more == 255);
            }
            source.Slice(read, literals).CopyTo(result.AsSpan(written, literals));
            read += literals;
            written += literals;
            if (read == source.Length) break;
            int distance = source[read] | source[read + 1] << 8;
            read += 2;
            if (distance == 0 || distance > written) throw new InvalidDataException("LZ4 回溯距离无效");
            int match = (token & 15) + 4;
            if ((token & 15) == 15)
            {
                byte more;
                do { more = source[read++]; match = checked(match + more); } while (more == 255);
            }
            if (match > size - written) throw new InvalidDataException("LZ4 解压长度越界");
            for (int i = 0; i < match; i++) { result[written] = result[written - distance]; written++; }
        }
        return written == size ? result : throw new InvalidDataException("LZ4 解压长度不符");
    }
}
