using System.Text;

namespace ReSekai;

readonly record struct ObjectId(string File, long Id);
sealed record ObjectInfo(long Id, int Offset, int Size, int Type);
sealed record AssetType(int ClassId, Schema? Schema);

sealed class AssetFile
{
    public required string Name { get; init; }
    public required byte[] Bytes { get; init; }
    public required bool Big { get; init; }
    public required string Version { get; init; }
    public required AssetType[] Types { get; init; }
    public required ObjectInfo[] Objects { get; init; }
    public required string[] Externals { get; init; }
    public string Bundle { get; set; } = "";

    public ObjectId Reference(Value pointer) => new(pointer["m_FileID"].Number is 0 ? Name : Externals[checked((int)pointer["m_FileID"].Number - 1)], pointer["m_PathID"].Number);
    public Binary Open(ObjectInfo item) => new(Bytes, Big, item.Offset, item.Size);
    public Value Read(ObjectInfo item, Func<Value, Schema[]>? script = null)
    {
        var type = Types[item.Type];
        var layout = type.Schema ?? Schemas.Classes.GetValueOrDefault(type.ClassId)?.Player
            ?? throw new NotSupportedException($"没有类 {type.ClassId} 的布局");
        var reader = Open(item);
        var value = Value.Read(layout, reader);
        if (type.ClassId == 114 && type.Schema is null && reader.Remaining > 0)
        {
            if (script is null) throw new InvalidDataException("读取脚本字段需要类型布局");
            var fields = script(value["m_Script"]);
            value = new(layout with { Fields = [.. layout.Children, .. fields] }, value.Items.Concat(fields.Select(f => Value.Read(f, reader))).ToArray());
        }
        if (reader.Remaining != 0) throw new InvalidDataException($"对象剩余 {reader.Remaining} 字节");
        return value;
    }

    public static AssetFile? Parse(string path, byte[] bytes)
    {
        if (bytes.Length < 48) return null;
        Binary header = new(bytes, true, 8);
        if (header.Int() != 22) return null;
        header.Int();
        bool big = header.Byte() != 0;
        header.Take(3);
        int metadata = checked((int)header.U32());
        long size = header.Long();
        long start = header.Long();
        header.Long();
        if (size != bytes.Length || metadata < 1 || start < 48 || start > size) throw new InvalidDataException($"序列化文件头损坏：{path}");
        Binary reader = new(bytes, big, 48, metadata);
        string version = reader.Text();
        reader.Int();
        bool trees = reader.Byte() != 0;
        AssetType[] types = Types(false);
        ObjectInfo[] objects = new ObjectInfo[reader.Count(24)];
        HashSet<long> ids = [];
        for (int i = 0; i < objects.Length; i++)
        {
            reader.Align();
            long id = reader.Long(), offset = reader.Long();
            int length = checked((int)reader.U32()), type = reader.Int();
            if (offset < 0 || start + offset + length > size || (uint)type >= types.Length || !ids.Add(id)) throw new InvalidDataException($"无效对象表：{path}/{id}");
            objects[i] = new(id, checked((int)(start + offset)), length, type);
        }
        int scriptCount = reader.Count(12);
        for (int i = 0; i < scriptCount; i++) { reader.Int(); reader.Align(); reader.Long(); }
        string[] externals = new string[reader.Count(21)];
        for (int i = 0; i < externals.Length; i++)
        {
            reader.Text(); reader.Take(20);
            externals[i] = Path.GetFileName(reader.Text().Replace('\\', '/'));
        }
        Types(true);
        reader.Text();
        return new() { Name = Path.GetFileName(path), Bytes = bytes, Big = big, Version = version, Types = types, Objects = objects, Externals = externals };

        AssetType[] Types(bool reference)
        {
            AssetType[] result = new AssetType[reader.Count(23)];
            for (int i = 0; i < result.Length; i++)
            {
                int classId = reader.Int();
                reader.Byte();
                short scriptIndex = unchecked((short)reader.U16());
                if (scriptIndex >= 0 || classId == 114) reader.Take(16);
                reader.Take(16);
                Schema? tree = trees ? ReadTree(reader) : null;
                if (trees)
                {
                    if (reference) { reader.Text(); reader.Text(); reader.Text(); }
                    else reader.Take(checked(reader.Count(4) * 4));
                }
                result[i] = new(classId, tree);
            }
            return result;
        }
    }

    static Schema ReadTree(Binary reader)
    {
        int count = reader.Count(32), size = reader.Int();
        var nodes = new (int Level, ushort Version, uint Type, uint Name, int Size, int Flags)[count];
        for (int i = 0; i < count; i++)
        {
            ushort version = reader.U16();
            int depth = reader.Byte();
            reader.Byte();
            uint type = reader.U32(), name = reader.U32();
            int bytes = reader.Int();
            reader.Int();
            int flags = reader.Int();
            reader.U64();
            nodes[i] = (depth, version, type, name, bytes, flags);
        }
        byte[] strings = reader.Take(size).ToArray();
        string Text(uint position)
        {
            byte[] source = (position & 0x80000000) != 0 ? Common : strings;
            int start = checked((int)(position & 0x7FFFFFFF));
            int end = Array.IndexOf(source, (byte)0, start);
            if (end < 0) throw new InvalidDataException("类型树字符串损坏");
            return Encoding.UTF8.GetString(source, start, end - start);
        }
        int cursor = 0;
        Schema Build(int depth)
        {
            var node = nodes[cursor++];
            if (node.Level != depth) throw new InvalidDataException("类型树层级损坏");
            List<Schema> fields = [];
            while (cursor < count && nodes[cursor].Level > depth) fields.Add(Build(depth + 1));
            return new(Text(node.Type), Text(node.Name), node.Size, node.Flags, node.Version, fields.ToArray());
        }
        if (count == 0) throw new InvalidDataException("类型树为空");
        Schema root = Build(0);
        if (cursor != count) throw new InvalidDataException("类型树包含多个根");
        return root;
    }

    // Unity 序列化格式的公共字符串表，偏移由前面的 UTF-8 字节长度决定。
    static readonly byte[] Common = Encoding.UTF8.GetBytes(string.Join('\0', new[] {
        "AABB", "AnimationClip", "AnimationCurve", "AnimationState", "Array", "Base", "BitField", "bitset", "bool", "char", "ColorRGBA", "Component", "data", "deque", "double", "dynamic_array", "FastPropertyName", "first", "float", "Font", "GameObject", "Generic Mono", "GradientNEW", "GUID", "GUIStyle", "int", "list", "long long", "map", "Matrix4x4f", "MdFour", "MonoBehaviour", "MonoScript", "m_ByteSize", "m_Curve", "m_EditorClassIdentifier", "m_EditorHideFlags", "m_Enabled", "m_ExtensionPtr", "m_GameObject", "m_Index", "m_IsArray", "m_IsStatic", "m_MetaFlag", "m_Name", "m_ObjectHideFlags", "m_PrefabInternal", "m_PrefabParentObject", "m_Script", "m_StaticEditorFlags", "m_Type", "m_Version", "Object", "pair", "PPtr<Component>", "PPtr<GameObject>", "PPtr<Material>", "PPtr<MonoBehaviour>", "PPtr<MonoScript>", "PPtr<Object>", "PPtr<Prefab>", "PPtr<Sprite>", "PPtr<TextAsset>", "PPtr<Texture>", "PPtr<Texture2D>", "PPtr<Transform>", "Prefab", "Quaternionf", "Rectf", "RectInt", "RectOffset", "second", "set", "short", "size", "SInt16", "SInt32", "SInt64", "SInt8", "staticvector", "string", "TextAsset", "TextMesh", "Texture", "Texture2D", "Transform", "TypelessData", "UInt16", "UInt32", "UInt64", "UInt8", "unsigned int", "unsigned long long", "unsigned short", "vector", "Vector2f", "Vector3f", "Vector4f", "m_ScriptingClassIdentifier", "Gradient", "Type*", "int2_storage", "int3_storage", "BoundsInt", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "FileSize", "Hash128", "RenderingLayerMask", "fixed_array", "EntityId", "LoadableReference", "" }));
}
