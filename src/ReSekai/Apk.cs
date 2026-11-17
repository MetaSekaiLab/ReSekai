using System.Buffers.Binary;
using System.IO.Compression;

namespace ReSekai;

sealed class Apk(string path) : IDisposable
{
    readonly ZipArchive zip = ZipFile.OpenRead(path);
    const string Data = "assets/bin/Data/";

    public byte[] Read(string name)
    {
        using var source = (zip.GetEntry(name) ?? throw new InvalidDataException($"APK 缺少 {name}")).Open();
        using MemoryStream result = new();
        source.CopyTo(result);
        return result.ToArray();
    }

    public void WriteNativePlugins(Output output)
    {
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("lib/arm64-v8a/", StringComparison.Ordinal)
            && e.Name.EndsWith(".so", StringComparison.Ordinal)))
        {
            // 引擎、游戏代码和 Burst 库由 Unity 根据恢复后的工程重新生成。
            if (entry.Name is "libil2cpp.so" or "libunity.so" or "libmain.so" or "lib_burst_generated.so") continue;
            string path = "Assets/Plugins/Android/libs/arm64-v8a/" + entry.Name;
            output.Write(path, Read(entry.FullName));
            output.Meta(path, Output.Guid("native-plugin:android-arm64:" + entry.Name), "PluginImporter",
                """
                  serializedVersion: 2
                  platformData:
                  - first:
                      Any:
                    second:
                      enabled: 0
                      settings: {}
                  - first:
                      Android: Android
                    second:
                      enabled: 1
                      settings:
                        CPU: ARM64

                """);
        }
    }

    public void Extract(string output)
    {
        byte[] image = Read("lib/arm64-v8a/libil2cpp.so");
        byte[] metadata = Decrypt(image, Read(Data + "Managed/Metadata/global-metadata.dat"));
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Join(output, "libil2cpp.so"), image);
        File.WriteAllBytes(Path.Join(output, "global-metadata.dat"), metadata);
    }

    public IEnumerable<(string Name, byte[] Bytes)> Entries()
    {
        var groups = zip.Entries.Where(e => e.FullName.StartsWith(Data, StringComparison.Ordinal)
                && e.Name.Length > 0 && !e.FullName.StartsWith(Data + "Managed/", StringComparison.Ordinal))
            .GroupBy(e => System.Text.RegularExpressions.Regex.Replace(e.FullName[Data.Length..], @"\.split\d+$", ""))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group.Count() == 1 && group.First().FullName[Data.Length..] == group.Key)
            {
                yield return (group.Key, Read(group.First().FullName));
                continue;
            }
            var parts = group.Select(e => (Entry: e, Index: int.Parse(e.FullName[(e.FullName.LastIndexOf(".split", StringComparison.Ordinal) + 6)..])))
                .OrderBy(p => p.Index).ToArray();
            if (!parts.Select(p => p.Index).SequenceEqual(Enumerable.Range(0, parts.Length))) throw new InvalidDataException($"分片不连续：{group.Key}");
            using MemoryStream joined = new();
            foreach (var part in parts)
            {
                using var stream = part.Entry.Open();
                stream.CopyTo(joined);
            }
            yield return (group.Key, joined.ToArray());
        }
    }

    public IEnumerable<(string Name, byte[] Bytes)> Streaming() => zip.Entries
        .Where(e => e.Name.Length > 0 && e.FullName.StartsWith("assets/", StringComparison.Ordinal) && !e.FullName.StartsWith("assets/bin/", StringComparison.Ordinal))
        .Where(e => e.Name.EndsWith(".acb", StringComparison.Ordinal) || e.Name.EndsWith(".acf", StringComparison.Ordinal)
            || e.FullName.StartsWith("assets/AssetBundle/", StringComparison.Ordinal) || e.Name == "google-services-desktop.json")
        .OrderBy(e => e.FullName, StringComparer.Ordinal).Select(e => (e.FullName[7..], Read(e.FullName)));

    static byte[] Decrypt(byte[] image, byte[] data)
    {
        const uint magic = 0xFAB11BAF;
        if (data.Length < 8) throw new InvalidDataException("metadata 太短");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) == magic) return data;
        if (image.Length < 64 || !image.AsSpan(0, 6).SequenceEqual(new byte[] { 127, 69, 76, 70, 2, 1 })
            || BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(18)) != 183) throw new InvalidDataException("需要 ARM64 ELF 映像");
        Binary elf = new(image, offset: 32);
        long table = elf.Long();
        elf.Position = 54;
        int stride = elf.U16(), count = elf.U16();
        List<(long File, long Address, long Size, bool Code)> segments = [];
        for (int index = 0; index < count; index++)
        {
            elf.Position = checked((int)(table + index * stride));
            uint type = elf.U32(), flags = elf.U32();
            long file = elf.Long(), address = elf.Long();
            elf.Long();
            long size = elf.Long();
            if (type == 1) segments.Add((file, address, size, (flags & 1) != 0));
        }
        int Offset(long address)
        {
            var segment = segments.Single(s => address >= s.Address && address - s.Address < s.Size);
            return checked((int)(segment.File + address - segment.Address));
        }
        uint At(long address) => BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(Offset(address), 4));
        long Extend(long value, int bits) => (value << (64 - bits)) >> (64 - bits);
        long? Address(uint page, uint add, long pc)
        {
            if ((page & 0x9F000000) != 0x90000000 || (add & 0xFF800000) != 0x91000000 || (page & 31) != ((add >> 5) & 31)) return null;
            long immediate = Extend(((page >> 5 & 0x7FFFF) << 2) | (page >> 29 & 3), 21);
            return (pc & ~4095L) + (immediate << 12) + ((long)(add >> 10 & 4095) << ((add & 0x400000) == 0 ? 0 : 12));
        }
        HashSet<long> names = [];
        int search = 0;
        while (search < image.Length)
        {
            int relative = image.AsSpan(search).IndexOf("global-metadata.dat\0"u8);
            if (relative < 0) break;
            int found = search + relative;
            foreach (var segment in segments.Where(s => found >= s.File && found - s.File < s.Size)) names.Add(segment.Address + found - segment.File);
            search = found + 1;
        }
        foreach (var segment in segments.Where(s => s.Code))
        for (long pc = segment.Address; pc + 160 < segment.Address + segment.Size; pc += 4)
        {
            uint instruction = At(pc);
            if ((instruction & 0x9F000000) != 0x90000000) continue;
            for (int next = 4; next < 48; next += 4)
            {
                if (Address(instruction, At(pc + next), pc) is not long name || !names.Contains(name)) continue;
                for (int call = next + 4; call < next + 96; call += 4)
                {
                    uint branch = At(pc + call);
                    if ((branch & 0xFC000000) != 0x94000000) continue;
                    long loader = pc + call + (Extend(branch & 0x3FFFFFF, 26) << 2);
                    long? key = Address(At(loader + 0x128), At(loader + 0x130), loader + 0x128);
                    if (key is not long keyAddress) break;
                    ReadOnlySpan<byte> bytes = image.AsSpan(Offset(keyAddress), 128);
                    uint header = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                    if (header != magic) break;
                    for (int n = 0; n < data.Length; n++) data[n] ^= bytes[n % 128];
                    if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) != 31) throw new InvalidDataException("只支持 metadata v31");
                    return data;
                }
            }
        }
        throw new InvalidDataException("未找到 metadata 解密密钥");
    }

    public void Dispose() => zip.Dispose();
}
