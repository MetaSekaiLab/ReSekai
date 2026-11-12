using System.Text.RegularExpressions;

namespace ReSekai;

sealed class Document(string file, long main, int classId, string guid, long[] objects)
{
    public string File { get; } = file;
    public long Main { get; } = main;
    public int ClassId { get; } = classId;
    public string Guid { get; set; } = guid;
    public long[] Objects { get; set; } = objects;
    public string Bundle { get; set; } = "";
    public TextureGroup? Texture { get; set; }
    public Dictionary<long, long> FileIds { get; } = [];
    public string Path { get; set; } = "";
    public bool Scene { get; init; }
    public bool Settings { get; init; }
    public bool Prefab { get; init; }
    public bool Imported => Texture is not null || ClassId is 49 or 48 or 72 or 128 or 83;
    public int RefType => Prefab || Scene || Imported ? 3 : 2;
    public long FileId(long id) => FileIds.TryGetValue(id, out var mapped) ? mapped : Imported ? ClassId * 100000L : id;
}

readonly record struct Target(string Guid, long Id, int Type);
readonly record struct LocalReference(long Id);

sealed class Project(Output output, Scripts scripts, ScriptLayout layouts, Packages packages)
{
    public Dictionary<string, List<Document>> Documents { get; } = [];
    public Dictionary<ObjectId, ScriptName> MonoScripts { get; } = [];
    AnimationNames? animationNames;
    public AnimationNames Animations => animationNames ??= new(layouts, MonoScripts, output);
    readonly Dictionary<ObjectId, Target> targets = [];
    readonly Dictionary<ObjectId, string> paths = [];
    readonly HashSet<string> usedPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> files = [];
    readonly HashSet<ObjectId> componentScripts = [];
    readonly Dictionary<string, string> folders = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Value> atlas = [];
    readonly Dictionary<ObjectId, Value> textures = [];
    readonly HashSet<ObjectId> emptyTextures = [];
    readonly Dictionary<ObjectId, Value> sprites = [];
    string[] tags = [];
    static readonly Dictionary<int, string> SettingsNames = new()
    {
        [5] = "TimeManager", [11] = "AudioManager", [13] = "InputManager", [19] = "Physics2DSettings", [30] = "GraphicsSettings", [47] = "QualitySettings",
        [55] = "DynamicsManager", [78] = "TagManager", [126] = "NavMeshAreas", [129] = "ProjectSettings", [310] = "UnityConnectSettings", [937362698] = "VFXManager"
    };
    const string Builtin = "0000000000000000f000000000000000";
    const string Default = "0000000000000000e000000000000000";

    public void Scan(List<AssetFile> batch)
    {
        Animations.Scan(batch);
        foreach (var file in batch)
        {
            if (file.Name is not ("unity_builtin_extra" or "unity default resources"))
            foreach (var item in file.Objects.Where(i => file.Types[i.Type].ClassId is 28 or 213))
            {
                Value value = Absolute(file.Read(item), file);
                if (file.Types[item.Type].ClassId == 28)
                {
                    ObjectId id = new(file.Name, item.Id);
                    textures.TryAdd(id, value.Set("image data", Array.Empty<byte>()));
                    if (value["image data"].Bytes.Length == 0 && value["m_StreamData"]["size"].Number == 0) emptyTextures.Add(id);
                }
                else sprites.TryAdd(new(file.Name, item.Id), value);
            }
            foreach (var item in file.Objects.Where(i => file.Types[i.Type].ClassId == 115))
            {
                Value value = file.Read(item);
                MonoScripts.TryAdd(new(file.Name, item.Id), new(ScriptName.AssemblyName(value["m_AssemblyName"].Text), value["m_Namespace"].Text,
                    value["m_ClassName"].Text.Length == 0 ? value["m_Name"].Text : value["m_ClassName"].Text));
            }
            foreach (var item in file.Objects.Where(i => file.Types[i.Type].ClassId == 114))
            {
                var header = Value.Read(Schemas.Classes[114].Player!, file.Open(item));
                if (header["m_GameObject"]["m_PathID"].Number != 0) componentScripts.Add(file.Reference(header["m_Script"]));
            }
            foreach (var item in file.Objects.Where(i => file.Types[i.Type].ClassId is 142 or 147 or 78 or 687078895))
            {
                Value value = file.Read(item);
                int classId = file.Types[item.Type].ClassId;
                if (classId == 78) tags = value["tags"].Items.Select(v => v.Text).ToArray();
                if (classId is 142 or 147)
                    foreach (Value pair in value["m_Container"].Items)
                    {
                        Value pointer = classId == 142 ? pair.Items[1]["asset"] : pair.Items[1];
                        string path = pair.Items[0].Text;
                        if (classId == 147) path = "Assets/Resources/" + path;
                        else if (path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) path = "Assets/" + path[7..];
                        else path = "Assets/Resources/" + path;
                        paths.TryAdd(file.Reference(pointer), path);
                    }
                if (classId == 687078895)
                    foreach (Value pair in value["m_RenderDataMap"].Items)
                        atlas.TryAdd(SpriteKey(pair.Items[0]), Absolute(pair.Items[1], file));
            }
        }
        foreach (var file in batch)
        {
            if (!files.Add(file.Name)) continue;
            if (file.Name is "unity_builtin_extra" or "unity default resources")
            {
                foreach (var item in file.Objects) targets[new(file.Name, item.Id)] = new(file.Name == "unity_builtin_extra" ? Builtin : Default, item.Id, 0);
                continue;
            }
            if (file.Version != Schemas.Version && !(file.Version == "0.0.0" && file.Types.All(t => t.Schema is not null)))
                throw new NotSupportedException($"{file.Name} 使用 Unity {file.Version}，当前支持 {Schemas.Version}");
            List<Document> documents = [];
            Documents[file.Name] = documents;
            var objects = file.Objects.Where(i => file.Types[i.Type].ClassId is not (115 or 142 or 147 or 141 or 1049 or 116 or 150 or 258)).ToArray();
            if (file.Name == "globalgamemanagers")
            {
                foreach (var item in objects.Where(i => SettingsNames.ContainsKey(file.Types[i.Type].ClassId)))
                {
                    int cls = file.Types[item.Type].ClassId;
                    documents.Add(new(file.Name, item.Id, cls, Output.Guid("settings:" + cls), [item.Id]) { Settings = true, Path = "ProjectSettings/" + SettingsNames[cls] + ".asset" });
                }
            }
            else if (Regex.IsMatch(file.Name, @"^level\d+$"))
                documents.Add(new(file.Name, objects[0].Id, file.Types[objects[0].Type].ClassId, Output.Guid("scene:" + file.Name), objects.Select(i => i.Id).ToArray()) { Scene = true, Path = "Assets/Scenes/" + file.Name + ".unity" });
            else if (Regex.IsMatch(file.Name, @"^[0-9a-f]{32}$") && objects.Length > 0)
            {
                bool prefab = objects.Any(i => file.Types[i.Type].ClassId == 1);
                var main = prefab ? Root(file, objects) : objects.OrderBy(i => Priority(file.Types[i.Type].ClassId)).ThenBy(i => i.Id).First();
                Add(main, objects, file.Name, prefab);
            }
            else
            {
                Dictionary<long, long> owner = [], parents = [], transforms = [];
                foreach (var item in objects.Where(i => file.Types[i.Type].ClassId is 1 or 4 or 224))
                {
                    Value value = file.Read(item);
                    if (file.Types[item.Type].ClassId == 1)
                        foreach (var component in value["m_Component"].Items) owner[component["component"]["m_PathID"].Number] = item.Id;
                    else
                    {
                        transforms[value["m_GameObject"]["m_PathID"].Number] = item.Id;
                        parents[item.Id] = value["m_Father"]["m_PathID"].Number;
                    }
                }
                Dictionary<long, List<ObjectInfo>> groups = [];
                foreach (var item in objects)
                {
                    long gameObject = file.Types[item.Type].ClassId == 1 ? item.Id : owner.GetValueOrDefault(item.Id);
                    long root = item.Id;
                    if (transforms.TryGetValue(gameObject, out long transform))
                    {
                        HashSet<long> visited = [];
                        while (parents.GetValueOrDefault(transform) is long parent && parent != 0)
                        {
                            if (!visited.Add(transform)) throw new InvalidDataException($"Transform 父节点形成循环：{file.Name}/{transform}");
                            transform = parent;
                        }
                        root = owner.GetValueOrDefault(transform, gameObject);
                    }
                    if (!groups.TryGetValue(root, out var group)) groups[root] = group = [];
                    group.Add(item);
                }
                foreach (var group in groups.OrderBy(p => p.Key))
                {
                    var main = group.Value.FirstOrDefault(i => i.Id == group.Key) ?? group.Value[0];
                    Add(main, group.Value.ToArray(), Output.Guid(file.Name + ":" + group.Key), file.Types[main.Type].ClassId == 1);
                }
            }
            foreach (var document in documents)
                foreach (long id in document.Objects) targets[new(file.Name, id)] = new(document.Guid, document.FileId(id), document.RefType);

            void Add(ObjectInfo main, ObjectInfo[] children, string guid, bool prefab)
            {
                if (guid == new string('0', 32) || guid is Builtin or Default) guid = Output.Guid("builtin-copy:" + guid);
                int cls = file.Types[main.Type].ClassId;
                // 导入器生成的资源单独写出，避免它们吞掉同文件的其他对象。
                if (cls is 48 or 49 or 72 or 83 or 128 && children.Length > 1)
                {
                    foreach (var child in children.Where(c => c.Id != main.Id)) Add(child, [child], Output.Guid(file.Name + ":" + child.Id), false);
                    children = [main];
                }
                if (cls is 28 or 213 && children.Any(c => file.Types[c.Type].ClassId is not (28 or 213)))
                {
                    foreach (var child in children.Where(c => file.Types[c.Type].ClassId is not (28 or 213))) Add(child, [child], Output.Guid(file.Name + ":" + child.Id), false);
                    children = children.Where(c => file.Types[c.Type].ClassId is 28 or 213).ToArray();
                }
                if (cls is not (28 or 213) && children.Any(c => file.Types[c.Type].ClassId is 28 or 213))
                {
                    foreach (var child in children.Where(c => file.Types[c.Type].ClassId is 28 or 213)) Add(child, [child], Output.Guid(file.Name + ":" + child.Id), false);
                    children = children.Where(c => file.Types[c.Type].ClassId is not (28 or 213)).ToArray();
                }
                string name;
                if (cls == 114 && file.Types[main.Type].Schema is null)
                {
                    var basic = Value.Read(Schemas.Classes[114].Player!, file.Open(main));
                    name = basic.Find("m_Name")?.Text ?? "MonoBehaviour";
                }
                else name = file.Read(main).Find("m_Name")?.Text ?? Schemas.Classes.GetValueOrDefault(cls)?.Name ?? "Object";
                if (cls == 48 && packages.Shaders.TryGetValue(name, out var shader))
                {
                    targets[new(file.Name, main.Id)] = new(shader, 4800000, 3);
                    return;
                }
                string extension = prefab ? ".prefab" : cls switch { 21 => ".mat", 48 => ".shader", 49 => ".bytes", 72 => ".compute", 74 => ".anim", 83 => ".wav", 91 => ".controller", 128 => ".ttf", 221 => ".overrideController", 319 => ".mask", 687078895 => ".spriteatlas", _ => ".asset" };
                string path = paths.GetValueOrDefault(new(file.Name, main.Id)) ?? $"Assets/Objects/{Output.Safe(name.Length == 0 ? "Object" : name)}_{guid[..8]}";
                if (Path.HasExtension(path)) path = path[..^Path.GetExtension(path).Length];
                path = Canonical(path + extension);
                if (!usedPaths.Add(path)) { path = path[..^extension.Length] + "_" + guid[..8] + extension; if (!usedPaths.Add(path)) throw new InvalidDataException($"资源路径重复：{path}"); }
                documents.Add(new(file.Name, main.Id, cls, guid, children.Select(i => i.Id).ToArray()) { Prefab = prefab, Path = path, Bundle = file.Bundle });
            }
        }
    }

    public void PrepareTextures()
    {
        var owners = Documents.Values.SelectMany(d => d).SelectMany(d => d.Objects.Select(id => (Id: new ObjectId(d.File, id), Document: d)))
            .Where(p => textures.ContainsKey(p.Id) || sprites.ContainsKey(p.Id)).ToDictionary(p => p.Id, p => p.Document);
        var originals = owners.Values.Distinct().Where(d => d.ClassId == 28).ToDictionary(d => d.Guid);
        foreach (var group in sprites.GroupBy(s =>
        {
            string guid = Yaml.Guid(s.Value["m_RenderDataKey"].Items[0]);
            return guid is Builtin or Default || guid == new string('0', 32) ? SpriteKey(s.Value["m_RenderDataKey"]) : guid;
        }))
        {
            var members = group.OrderBy(s => s.Key.File, StringComparer.Ordinal).ThenBy(s => s.Key.Id).ToArray();
            var distinct = members.GroupBy(s => SpriteKey(s.Value["m_RenderDataKey"])).ToArray();
            string guid = group.Key.Length == 32 ? group.Key : Output.Guid("sprites:" + group.Key);
            Document document = originals.GetValueOrDefault(guid) ?? owners[members[0].Key];
            if (document.ClassId is not (28 or 213)) throw new InvalidDataException($"Sprite 与其他资源混在同一文档：{document.Path}");
            ObjectId? source = document.Objects.Select(id => new ObjectId(document.File, id)).Where(textures.ContainsKey).Select(id => (ObjectId?)id).FirstOrDefault();
            if (source is null)
            {
                var pointer = members[0].Value["m_RD"]["texture"].Data;
                if (pointer is ObjectId texture && textures.ContainsKey(texture) && !atlas.ContainsKey(SpriteKey(members[0].Value["m_RenderDataKey"])))
                {
                    source = texture;
                    var previous = owners[texture];
                    if (previous.ClassId == 28 && previous != document) Documents[previous.File].Remove(previous);
                }
            }
            document.Guid = guid;
            document.Texture = new(source, distinct.Select(g =>
            {
                var chosen = g.First();
                long id = distinct.Length == 1 ? 21300000 : Convert.ToInt64(Output.Guid("sprite:" + g.Key)[..15], 16);
                Value data = atlas.GetValueOrDefault(g.Key) ?? chosen.Value["m_RD"];
                foreach (var copy in g) targets[copy.Key] = new(guid, id, 3);
                return new SpritePart(chosen.Key, chosen.Value, data, id);
            }).ToArray());
            foreach (var member in members)
            {
                var previous = owners[member.Key];
                if (previous != document && previous.Objects.All(id => sprites.ContainsKey(new(previous.File, id)))) Documents[previous.File].Remove(previous);
            }
            if (source is { } textureId) targets[textureId] = new(guid, 2800000, 3);
            foreach (long id in document.Objects)
            {
                var key = new ObjectId(document.File, id);
                document.FileIds[id] = textures.ContainsKey(key) ? 2800000 : targets[key].Id;
            }
            PngPath(document);
        }
        foreach (var pair in textures)
        {
            if (emptyTextures.Contains(pair.Key)) continue;
            if (!owners.TryGetValue(pair.Key, out var document) || document.Texture is not null || !Documents[document.File].Contains(document)) continue;
            if (document.ClassId != 28) continue;
            document.Texture = new(pair.Key, []);
            document.FileIds[pair.Key.Id] = 2800000;
            targets[pair.Key] = new(document.Guid, 2800000, 3);
            PngPath(document);
        }
        void PngPath(Document document)
        {
            if (document.Path.EndsWith(".png", StringComparison.Ordinal)) return;
            string path = Path.ChangeExtension(document.Path, ".png");
            if (!usedPaths.Add(path))
            {
                path = Path.ChangeExtension(path, null) + "_" + document.Guid[..8] + ".png";
                if (!usedPaths.Add(path)) throw new InvalidDataException("纹理路径重复：" + path);
            }
            document.Path = path;
        }
    }

    public void BindScripts()
    {
        foreach (var pair in MonoScripts)
        {
            if (targets.ContainsKey(pair.Key)) continue;
            if (pair.Value.Assembly.Length == 0)
            {
                var placeholder = scripts.Stripped(pair.Value, componentScripts.Contains(pair.Key));
                targets[pair.Key] = new(placeholder.Guid, placeholder.Id, 3);
                continue;
            }
            if (scripts.Target(pair.Value) is { } target) targets[pair.Key] = new(target.Guid, target.Id, 3);
        }
    }

    public Value Read(AssetFile file, ObjectInfo item) => file.Read(item, pointer =>
        MonoScripts.TryGetValue(file.Reference(pointer), out var script) ? layouts.For(script) : throw new InvalidDataException($"脚本不在输入中：{file.Reference(pointer)}"));

    public string Reference(AssetFile file, Document document, Value? pointer)
    {
        if (pointer is null) return "{fileID: 0}";
        if (pointer.Data is LocalReference local) return $"{{fileID: {local.Id}}}";
        var id = pointer.Data is ObjectId absolute ? absolute : file.Reference(pointer);
        if (id.Id == 0) return "{fileID: 0}";
        if (!targets.TryGetValue(id, out var target))
        {
            output.Note("引用", document.Path, $"目标未导出：{id.File}/{id.Id}", true);
            return "{fileID: 0}";
        }
        return target.Guid == document.Guid ? $"{{fileID: {target.Id}}}" : $"{{fileID: {target.Id}, guid: {target.Guid}, type: {target.Type}}}";
    }

    public (Schema Schema, Value Value) Editor(AssetFile file, ObjectInfo item, Value value)
    {
        int cls = file.Types[item.Type].ClassId;
        Schema schema = Schemas.Classes.GetValueOrDefault(cls)?.Editor ?? throw new NotSupportedException($"没有编辑器布局：{cls}");
        List<Value> fields = [.. value.Items];
        void Set(string name, object data)
        {
            var field = schema.Children.First(c => c.Name == name);
            fields.RemoveAll(v => v.Schema.Name == name);
            fields.Add(new(field, data));
        }
        if (cls == 114)
        {
            int common = Schemas.Classes[114].Player!.Children.Length;
            schema = schema with { Fields = [.. schema.Children, .. value.Schema.Children.Skip(common)] };
        }
        if (cls == 1)
        {
            int tag = (int)value["m_Tag"].Number;
            string name = tag switch { 0 => "Untagged", 1 => "Respawn", 2 => "Finish", 3 => "EditorOnly", 5 => "MainCamera", 6 => "Player", 7 => "GameController", _ when tag >= 20000 && tag - 20000 < tags.Length => tags[tag - 20000], _ => throw new InvalidDataException($"未知标签 {tag}") };
            Set("m_TagString", name);
        }
        foreach (var field in schema.Children)
        {
            if (value.Find(field.Name) is not null) continue;
            object? initial = field.Name switch
            {
                "m_ScaleInLightmap" => 1f, "m_ReceiveGI" => 1, "m_SelectedEditorRenderState" => 3,
                "m_MinimumChartSize" => 4, "m_AutoUVMaxDistance" => .5f, "m_AutoUVMaxAngle" => 89f,
                "m_MeshOptimizationFlags" => -1, _ => null
            };
            if (initial is not null) Set(field.Name, initial);
        }
        if (cls is 4 or 224)
        {
            var q = value["m_LocalRotation"];
            var rotation = System.Numerics.Quaternion.Normalize(new(q["x"].Float, q["y"].Float, q["z"].Float, q["w"].Float));
            float x = rotation.X, y = rotation.Y, z = rotation.Z, w = rotation.W;
            float pitch = MathF.Asin(Math.Clamp(2 * (w * x - y * z), -1f, 1f));
            bool singular = MathF.Abs(MathF.Cos(pitch)) < .00001f;
            float yaw = singular ? MathF.Atan2(2 * (w * y - x * z), 1 - 2 * (y * y + z * z)) : MathF.Atan2(2 * (x * z + w * y), 1 - 2 * (x * x + y * y));
            float roll = singular ? 0 : MathF.Atan2(2 * (x * y + w * z), 1 - 2 * (x * x + z * z));
            var hint = schema.Children.First(c => c.Name == "m_LocalEulerAnglesHint");
            Set(hint.Name, Value.Make(hint, ("x", pitch * 180 / MathF.PI), ("y", yaw * 180 / MathF.PI), ("z", roll * 180 / MathF.PI)).Data!);
        }
        if (cls == 129)
        {
            Set("allowUnsafeCode", true);
            Set("apiCompatibilityLevel", 3);
            foreach (var setting in new[] { ("apiCompatibilityLevelPerPlatform", 3), ("scriptingBackend", 1) })
            {
                var pair = schema.Children.First(c => c.Name == setting.Item1).Element!;
                Set(setting.Item1, new[] { new Value(pair, new[] { new Value(pair.Children[0], "Android"), new Value(pair.Children[1], setting.Item2) }) });
            }
        }
        if (cls == 687078895)
        {
            var entries = value["m_RenderDataMap"].Items.Select(v => v.Items[1]).ToArray();
            var page = entries.Select(v => textures.GetValueOrDefault(file.Reference(v["texture"]))).FirstOrDefault(t => t is not null);
            var data = schema.Children.First(c => c.Name == "m_EditorData");
            var sampling = page?.Find("m_TextureSettings");
            int size = page is null ? 2048 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(32, Math.Max(page["m_Width"].Number, page["m_Height"].Number)));
            var textureSettings = Value.Make(data.Children.First(c => c.Name == "textureSettings"),
                ("anisoLevel", sampling?["m_Aniso"].Data ?? 1), ("filterMode", sampling?["m_FilterMode"].Data ?? 1),
                ("maxTextureSize", size), ("compressionQuality", 50), ("textureCompression", 1),
                ("generateMipMaps", page?["m_MipCount"].Number > 1), ("readable", page?["m_IsReadable"].Number != 0 && page is not null), ("sRGB", page is null || page["m_ColorSpace"].Number == 1));
            var packing = Value.Make(data.Children.First(c => c.Name == "packingSettings"), ("padding", 4), ("blockOffset", 1),
                ("enableRotation", entries.Any(p => ((p["settingsRaw"].Number >> 2) & 15) != 0)), ("enableTightPacking", entries.Any(p => (p["settingsRaw"].Number & 2) != 0)));
            Set(data.Name, Value.Make(data, ("textureSettings", textureSettings), ("packingSettings", packing),
                ("variantMultiplier", 1f), ("packables", value["m_PackedSprites"]), ("bindAsDefault", true)).Data!);
            Set("m_RenderDataMap", Array.Empty<Value>());
        }
        if (cls == 213 && atlas.TryGetValue(SpriteKey(value["m_RenderDataKey"]), out var packed))
        {
            var original = value["m_RD"];
            Set("m_RD", original.Items.Select(v => packed.Find(v.Schema.Name) is { } replacement ? replacement with { Schema = v.Schema } : v).ToArray());
            Set("m_SpriteAtlas", value["m_SpriteAtlas"].Items.Select(v => v with { Data = 0 }).ToArray());
        }
        if (cls == 74)
            return (schema, Animation.Convert(value, schema, Animations.For(file, value,
                (reason, error) => output.Note("动画", file.Name + "/" + item.Id, reason, error))));
        return (schema, new(value.Schema, fields.ToArray()));
    }

    string Canonical(string path)
    {
        string[] segments = path.Split('/').Select(Output.Safe).ToArray();
        for (int i = 1; i < segments.Length - 1; i++)
        {
            string key = string.Join('/', segments.Take(i + 1));
            if (folders.TryGetValue(key, out var known)) segments[i] = known;
            else folders[key] = segments[i];
        }
        if (string.Join('/', segments).Length > 180) return "Assets/Objects/" + segments[^1];
        return string.Join('/', segments);
    }
    static int Priority(int cls) => cls switch { 1 => 0, 128 => 1, 114 => 2, 74 => 3, 21 => 4, _ => 5 };
    static ObjectInfo Root(AssetFile file, ObjectInfo[] objects)
    {
        foreach (var transform in objects.Where(i => file.Types[i.Type].ClassId is 4 or 224))
        {
            var value = file.Read(transform);
            if (value["m_Father"]["m_PathID"].Number == 0) return objects.First(o => o.Id == value["m_GameObject"]["m_PathID"].Number);
        }
        throw new InvalidDataException($"预制体没有根节点：{file.Name}");
    }
    static string SpriteKey(Value pair) => Yaml.Guid(pair.Items[0]) + ":" + pair.Items[1].Number;
    static Value Absolute(Value value, AssetFile file) => value.Schema.Pointer ? value with { Data = file.Reference(value) }
        : value.Data is Value[] items ? value with { Data = items.Select(v => Absolute(v, file)).ToArray() } : value;
}
