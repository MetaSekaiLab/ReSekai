namespace ReSekai;

sealed record ResourceUnit(string Name, Func<List<(string Name, byte[] Bytes)>> Load);

sealed class Resources
{
    public List<ResourceUnit> Units { get; } = [];
    readonly Dictionary<string, ResourceUnit> owners = new(StringComparer.Ordinal);
    Dictionary<string, byte[]> loaded = [];

    public Resources(Apk apk, string? directory)
    {
        Units.Add(new("APK", () => apk.Entries().SelectMany(e => Bundle.Matches(e.Bytes) ? Bundle.Read(e.Bytes) : new List<(string, byte[])> { e }).ToList()));
        if (directory is null) return;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            byte[] head = new byte[8];
            int size = stream.Read(head);
            if (!Bundle.Matches(head.AsSpan(0, size))) throw new InvalidDataException($"资源目录含非资源包文件：{file}");
            string path = file;
            Units.Add(new(Path.GetRelativePath(directory, file).Replace('\\', '/'), () => Bundle.Read(File.ReadAllBytes(path))));
        }
    }

    public List<AssetFile> Open(ResourceUnit unit)
    {
        var entries = unit.Load();
        loaded = entries.ToDictionary(e => Path.GetFileName(e.Name), e => e.Bytes, StringComparer.Ordinal);
        foreach (string name in loaded.Keys) owners.TryAdd(name, unit);
        var files = loaded.Select(e => AssetFile.Parse(e.Key, e.Value)).OfType<AssetFile>().ToList();
        if (unit.Name != "APK") foreach (var file in files) file.Bundle = unit.Name;
        return files;
    }

    public byte[] Read(Value stream)
    {
        bool audio = stream.Find("m_Source") is not null;
        string name = Path.GetFileName(stream[audio ? "m_Source" : "path"].Text.Replace('\\', '/'));
        int offset = checked((int)stream[audio ? "m_Offset" : "offset"].Number), size = checked((int)stream[audio ? "m_Size" : "size"].Number);
        if (!loaded.TryGetValue(name, out byte[]? bytes))
        {
            if (!owners.TryGetValue(name, out var owner)) throw new InvalidDataException($"缺少外部资源：{name}");
            bytes = owner.Load().First(e => Path.GetFileName(e.Name) == name).Bytes;
        }
        return bytes.AsSpan(offset, size).ToArray();
    }

    public Value Object(ObjectId id)
    {
        if (!loaded.TryGetValue(id.File, out var bytes))
        {
            if (!owners.TryGetValue(id.File, out var unit)) throw new InvalidDataException($"资源文件未索引：{id.File}");
            bytes = unit.Load().First(p => Path.GetFileName(p.Name) == id.File).Bytes;
        }
        var file = AssetFile.Parse(id.File, bytes) ?? throw new InvalidDataException($"不是序列化资源：{id.File}");
        return file.Read(file.Objects.First(o => o.Id == id.Id));
    }
}
