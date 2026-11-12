using System.Buffers.Binary;
using System.Text;

namespace ReSekai;

sealed class AnimationNames(ScriptLayout layouts, Dictionary<ObjectId, ScriptName> scripts, Output output)
{
    sealed class Names
    {
        public readonly Dictionary<uint, Dictionary<string, HashSet<string>>> Entries = [];
        public void Add(uint hash, string name, string source)
        {
            if (!Entries.TryGetValue(hash, out var names)) Entries[hash] = names = new(StringComparer.Ordinal);
            if (!names.TryGetValue(name, out var sources)) names[name] = sources = new(StringComparer.Ordinal);
            sources.Add(source);
        }
        public string[] Find(uint hash, HashSet<string>? sources = null)
        {
            if (!Entries.TryGetValue(hash, out var names)) return [];
            var nearby = sources is null ? [] : names.Where(p => p.Value.Overlaps(sources)).Select(p => p.Key).ToArray();
            return (nearby.Length > 0 ? nearby : names.Keys.ToArray()).Order(StringComparer.Ordinal).ToArray();
        }
    }
    readonly Names paths = new(), materials = new(), shapes = new();
    readonly Dictionary<ObjectId, string> objects = [];
    readonly Dictionary<ObjectId, (ObjectId Parent, ObjectId Object)> transforms = [];
    readonly Dictionary<ObjectId, Schema> scriptTrees = [];
    readonly Dictionary<string, Names> fieldNames = [];
    readonly HashSet<string> files = [], vocabulary = new(StringComparer.Ordinal);
    readonly HashSet<uint> wanted = [];
    readonly Dictionary<string, Names> guesses = new(StringComparer.Ordinal);
    (string Name, byte[] Bytes)[] words = [];
    static readonly uint[] crcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint crc = (uint)i;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }).ToArray();
    static uint Append(uint crc, ReadOnlySpan<byte> text)
    {
        foreach (byte b in text) crc = crcTable[(crc ^ b) & 255] ^ (crc >> 8);
        return crc;
    }
    public static uint Hash(string text) => ~Append(uint.MaxValue, Encoding.UTF8.GetBytes(text));

    public void Scan(IEnumerable<AssetFile> batch)
    {
        foreach (var file in batch)
        {
            if (!files.Add(file.Name)) continue;
            foreach (var item in file.Objects)
            {
                int cls = file.Types[item.Type].ClassId;
                if (cls == 114 && file.Types[item.Type].Schema is { } tree)
                {
                    var header = Value.Read(Schemas.Classes[114].Player!, file.Open(item));
                    scriptTrees.TryAdd(file.Reference(header["m_Script"]), tree);
                }
                if (cls is not (1 or 4 or 224 or 21 or 43 or 48 or 49 or 74 or 90)) continue;
                ObjectId id = new(file.Name, item.Id);
                try
                {
                    if (cls == 49)
                    {
                        var reader = file.Open(item); reader.Take(reader.Count()); reader.Align();
                        ReadMocPaths(reader.Take(reader.Count()), name => paths.Add(Hash(name), name, file.Name));
                        continue;
                    }
                    Value value = file.Read(item);
                    switch (cls)
                    {
                        case 1:
                            objects[id] = value["m_Name"].Text;
                            vocabulary.Add(value["m_Name"].Text);
                            break;
                        case 4 or 224:
                            transforms[id] = (file.Reference(value["m_Father"]), file.Reference(value["m_GameObject"]));
                            break;
                        case 74:
                            foreach (var binding in value["m_ClipBindingConstant"]["genericBindings"].Items) wanted.Add((uint)binding["path"].Number);
                            break;
                        case 90:
                            foreach (var pair in value["m_TOS"].Items)
                                if (Hash(pair.Items[1].Text) == (uint)pair.Items[0].Number) paths.Add((uint)pair.Items[0].Number, pair.Items[1].Text, file.Name);
                            break;
                        case 43:
                            foreach (var channel in value["m_Shapes"]["channels"].Items) shapes.Add((uint)channel["nameHash"].Number, channel["name"].Text, file.Name);
                            break;
                        case 48:
                            foreach (var property in value["m_ParsedForm"]["m_PropInfo"]["m_Props"].Items) Material(property["m_Name"].Text, file.Name);
                            break;
                        case 21:
                            foreach (var group in value["m_SavedProperties"].Items)
                            foreach (var pair in group.Items) Material(pair.Items[0].Text, file.Name);
                            break;
                    }
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
                {
                    output.Note("动画索引", file.Name + "/" + item.Id, error.Message, true);
                }
            }
        }
    }
    void Material(string name, string source)
    {
        materials.Add(Hash(name) & 0xfffffff, name, source);
        materials.Add(Hash(name + "_ST") & 0xfffffff, name + "_ST", source);
    }
    internal static void ReadMocPaths(ReadOnlySpan<byte> data, Action<string> add)
    {
        if (!data.StartsWith("MOC3"u8)) return;
        if (data.Length < 0x10c || data[4] is < 1 or > 6 || data[5] != 0) throw new NotSupportedException("moc3 版本或字节序尚未支持");
        int counts = BinaryPrimitives.ReadInt32LittleEndian(data[0x40..]);
        if (counts < 0 || counts > data.Length - 24) throw new InvalidDataException("moc3 数量表越界");
        foreach (var section in new[] { (Offset: 0x4c, Count: counts, Prefix: "Parts/"), (Offset: 0x108, Count: counts + 20, Prefix: "Parameters/") })
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(data[section.Count..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(data[section.Offset..]);
            if (count < 0 || offset < 0 || offset + (long)count * 64 > data.Length) throw new InvalidDataException("moc3 名称表越界");
            for (int i = 0; i < count; i++)
            {
                var slot = data.Slice(offset + i * 64, 64); int end = slot.IndexOf((byte)0);
                string name = section.Prefix + Encoding.UTF8.GetString(slot[..(end < 0 ? 64 : end)]);
                add(name);
            }
        }
    }
    public void Prepare()
    {
        paths.Add(0, "", "");
        foreach (var pair in transforms)
        {
            List<string> chain = [];
            HashSet<ObjectId> visited = [];
            for (ObjectId at = pair.Key; at.Id != 0 && transforms.TryGetValue(at, out var node); at = node.Parent)
            {
                if (!visited.Add(at)) throw new InvalidDataException("动画索引遇到循环层级：" + at);
                if (!objects.TryGetValue(node.Object, out string? name)) break;
                chain.Add(name);
            }
            string path = "";
            for (int length = 0; length < chain.Count - 1; length++)
            {
                path = length == 0 ? chain[0] : chain[length] + "/" + path;
                uint hash = Hash(path);
                if (wanted.Contains(hash)) paths.Add(hash, path, pair.Key.File);
            }
        }
        words = vocabulary.Where(n => n.Length > 0).SelectMany(n => new[] { n, n + "(Clone)" }).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select(n => (n, Encoding.UTF8.GetBytes(n))).ToArray();
        transforms.Clear(); objects.Clear(); vocabulary.Clear();
    }

    public Resolver For(AssetFile file, Value clip, Action<string, bool> issue) => new(this, file, clip, issue);
    public sealed class Resolver
    {
        readonly AnimationNames owner;
        readonly AssetFile file;
        readonly Action<string, bool> issue;
        readonly HashSet<string> sources, reported = [];
        readonly Dictionary<uint, string> resolved = [];
        readonly string[] prefixes;
        public Resolver(AnimationNames owner, AssetFile file, Value clip, Action<string, bool> issue)
        {
            this.owner = owner; this.file = file; this.issue = issue;
            sources = [file.Name, .. file.Externals];
            var known = clip["m_ClipBindingConstant"]["genericBindings"].Items.Select(b => (uint)b["path"].Number).Distinct()
                .SelectMany(h => owner.paths.Find(h, sources)).ToArray();
            HashSet<string> candidates = [""];
            foreach (string path in known)
                for (int at = path.Length; at > 0; at = path.LastIndexOf('/', at - 1)) candidates.Add(path[..at] + "/");
            prefixes = candidates.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).Take(64).ToArray();
        }
        string? Select(Names index, uint hash, string kind)
        {
            string[] names = index.Find(hash, sources);
            if (names.Length == 1) return names[0];
            if (names.Length > 1) Note($"{kind} CRC 0x{hash:X8} 有多个候选：{string.Join(" | ", names.Take(4))}", true);
            return null;
        }
        void Note(string message, bool error) { if (reported.Add(message)) issue(message, error); }
        public string Path(uint hash)
        {
            if (resolved.TryGetValue(hash, out string? cached)) return cached;
            string? path = Select(owner.paths, hash, "路径");
            if (path is null && !owner.paths.Entries.ContainsKey(hash))
            {
                HashSet<string> matches = new(StringComparer.Ordinal);
                foreach (string prefix in prefixes)
                {
                    if (!owner.guesses.TryGetValue(prefix, out var table))
                    {
                        owner.guesses[prefix] = table = new();
                        uint start = Append(uint.MaxValue, Encoding.UTF8.GetBytes(prefix));
                        foreach (var word in owner.words)
                        {
                            uint candidate = ~Append(start, word.Bytes);
                            if (owner.wanted.Contains(candidate) && !owner.paths.Entries.ContainsKey(candidate)) table.Add(candidate, prefix + word.Name, "");
                        }
                    }
                    matches.UnionWith(table.Find(hash));
                }
                if (matches.Count == 1)
                {
                    path = matches.Single();
                    Note($"路径为规则推断：0x{hash:X8} → {path}", false);
                }
                else if (matches.Count > 1) Note($"路径 CRC 0x{hash:X8} 命中多个规则候选", true);
            }
            if (path is null) Note($"路径名称未确定：0x{hash:X8}", true);
            return resolved[hash] = path ?? $"__resekai_path_{hash:X8}";
        }
        public string Attribute(Value binding)
        {
            uint hash = (uint)binding["attribute"].Number;
            int cls = (int)binding["typeID"].Number, custom = (int)binding["customType"].Number;
            string? name = custom switch
            {
                21 => $"m_Materials.Array.data[{hash}]",
                22 => Select(owner.materials, hash & 0xfffffff, "材质属性") is { } material ? "material." + material
                    + ((hash & 0x80000000) != 0 ? "" : "." + ((hash & 0x40000000) != 0 ? "rgba" : "xyzw")[(int)((hash >> 28) & 3)]) : null,
                20 => Select(owner.shapes, hash, "BlendShape") is { } shape ? "blendShape." + shape : null,
                23 when hash == 0 => "m_Sprite",
                8 => Animation.MuscleName(hash),
                _ => Select(owner.Fields(cls, file.Reference(binding["script"])), hash, "属性")
            };
            if (name is null) Note($"属性名称未确定：类 {cls}，自定义类型 {custom}，CRC 0x{hash:X8}", true);
            return name ?? $"__resekai_attr_{hash:X8}";
        }
    }
    Names Fields(int cls, ObjectId script)
    {
        bool known = scripts.TryGetValue(script, out var name);
        string key = cls == 114 ? "script:" + (known ? name.Key : script.ToString()) : "class:" + cls;
        if (fieldNames.TryGetValue(key, out var result)) return result;
        fieldNames[key] = result = new();
        if (Schemas.Classes.TryGetValue(cls == 25 ? 23 : cls, out var type))
        {
            Visit(type.Player?.Children ?? [], "", 0);
            Visit(type.Editor?.Children ?? [], "", 0);
        }
        if (cls == 114)
        {
            try
            {
                if (scriptTrees.TryGetValue(script, out var tree)) Visit(tree.Children, "", 0);
                else if (known) Visit(layouts.For(name), "", 0);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { output.Note("动画属性布局", key, error.Message, true); }
        }
        if (cls == 20)
            foreach (string field in new[] { "field of view", "near clip plane", "far clip plane", "orthographic size" }) result.Add(Hash(field), field, "");
        return result;
        void Visit(Schema[] fields, string prefix, int depth)
        {
            foreach (var field in fields)
            {
                if (field.Element is not null || field.Type == "string") continue;
                string path = prefix + field.Name;
                result.Add(Hash(path), path, "");
                if (!field.Pointer && depth < 16) Visit(field.Children, path + ".", depth + 1);
            }
        }
    }
}
