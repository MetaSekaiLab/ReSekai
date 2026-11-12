using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ReSekai;

sealed record Schema(string Type, string Name, int Size = -1, int Flags = 0, int Version = 1, Schema[]? Fields = null)
{
    public Schema[] Children => Fields ?? [];
    public Schema? Element => Children.FirstOrDefault()?.Type == "Array" ? Children[0].Children[1] : null;
    public bool Align => (Flags & 0x4000) != 0;
    public bool Pointer => Type.StartsWith("PPtr<", StringComparison.Ordinal);
    public bool Integer => Type is "bool" or "char" or "SInt8" or "UInt8" or "SInt16" or "UInt16" or "short" or "unsigned short"
        or "SInt32" or "UInt32" or "int" or "unsigned int" or "Type*" or "SInt64" or "UInt64" or "long long" or "unsigned long long" or "FileSize";
    public static Schema FromJson(JsonElement value) => new(value[0].GetString()!, value[1].GetString()!, value[2].GetInt32(),
        value[3].GetInt32(), value[5].GetInt32(), value[6].EnumerateArray().Select(FromJson).ToArray());
    public Schema Rename(string name) => this with { Name = name };
    public static Schema Array(string name, Schema element, bool align = true) => new("vector", name, Fields:
        [new("Array", "Array", Flags: align ? 0x4000 : 0, Fields: [new("int", "size", 4), element.Rename("data")])]);
}

sealed record ClassSchema(int Id, string Name, Schema? Player, Schema? Editor);

static class Schemas
{
    public static readonly string Version;
    public static readonly Dictionary<int, ClassSchema> Classes;
    public static readonly Dictionary<string, Schema> Native = [];

    static Schemas()
    {
        using var data = Data("typetree.json.gz");
        Version = data.RootElement.GetProperty("version").GetString()!;
        Classes = data.RootElement.GetProperty("classes").EnumerateArray().Select(c => new ClassSchema(c.GetProperty("id").GetInt32(),
            c.GetProperty("name").GetString()!, c.GetProperty("release").ValueKind == JsonValueKind.Null ? null : Schema.FromJson(c.GetProperty("release")),
            c.GetProperty("editor").ValueKind == JsonValueKind.Null ? null : Schema.FromJson(c.GetProperty("editor")))).ToDictionary(c => c.Id);
        foreach (var cls in Classes.Values) if (cls.Player is { } root) Visit(root);
        void Visit(Schema node)
        {
            bool valid = node.Type switch
            {
                "AnimationCurve" => node.Element is null && node.Children.FirstOrDefault()?.Element?.Children.Any(c => c.Name == "value" && c.Type == "float") == true,
                "ColorRGBA" => node.Children.Length == 4,
                _ => node.Children.Length > 0
            };
            if (valid) Native.TryAdd(node.Type, node);
            foreach (var child in node.Children) Visit(child);
        }
    }

    public static JsonDocument Data(string name)
    {
        using var stream = typeof(Schemas).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException($"缺少格式数据：{name}");
        using GZipStream compressed = new(stream, CompressionMode.Decompress);
        return JsonDocument.Parse(compressed);
    }
}

sealed record Value(Schema Schema, object? Data)
{
    public Value[] Items => Data as Value[] ?? [];
    public Value[] Elements
    {
        get
        {
            if (Data is not byte[] bytes || Schema.Element is not { Integer: true } element) return Items;
            Binary reader = new(bytes);
            List<Value> values = [];
            while (reader.Remaining > 0) values.Add(Read(element, reader));
            return values.ToArray();
        }
    }
    public Value? Find(string name) => Items.FirstOrDefault(v => v.Schema.Name == name);
    public Value this[string name] => Find(name) ?? throw new InvalidDataException($"{Schema.Type} 缺少字段 {name}");
    public long Number => Data is null ? 0 : Convert.ToInt64(Data, System.Globalization.CultureInfo.InvariantCulture);
    public float Float => Data is null ? 0 : Convert.ToSingle(Data, System.Globalization.CultureInfo.InvariantCulture);
    public string Text => Data as string ?? "";
    public byte[] Bytes => Data as byte[] ?? [];
    public Value Set(string name, object? data) => new(Schema, Items.Select(v => v.Schema.Name == name ? v with { Data = data } : v).ToArray());
    public static Value Make(Schema schema, params (string Name, object? Data)[] fields) => new(schema, fields.Select(f =>
        new Value(schema.Children.First(c => c.Name == f.Name), f.Data is Value value ? value.Data : f.Data)).ToArray());

    public static Value Read(Schema schema, Binary reader)
    {
        object? data;
        bool align = schema.Align;
        if (schema.Type == "string")
        {
            data = Encoding.UTF8.GetString(reader.Take(reader.Count()));
            align = true;
        }
        else if (schema.Type == "TypelessData") data = reader.Take(reader.Count()).ToArray();
        else if (schema.Element is { } element)
        {
            int count = reader.Count(Math.Max(1, element.Size));
            align |= schema.Children[0].Align;
            if (element.Integer && !element.Align && !reader.Big) data = reader.Take(checked(count * element.Size)).ToArray();
            else
            {
                Value[] values = new Value[count];
                for (int index = 0; index < count; index++) values[index] = Read(element, reader);
                data = values;
            }
        }
        else if (schema.Children.Length > 0) data = schema.Children.Select(c => Read(c, reader)).ToArray();
        else data = schema.Type switch
        {
            "bool" => reader.Byte() != 0,
            "char" or "UInt8" => reader.Byte(),
            "SInt8" => unchecked((sbyte)reader.Byte()),
            "SInt16" or "short" => unchecked((short)reader.U16()),
            "UInt16" or "unsigned short" => reader.U16(),
            "int" or "SInt32" or "Type*" => reader.Int(),
            "unsigned int" or "UInt32" => reader.U32(),
            "SInt64" or "long long" => reader.Long(),
            "UInt64" or "unsigned long long" or "FileSize" => reader.U64(),
            "float" => BitConverter.Int32BitsToSingle(reader.Int()),
            "double" => BitConverter.Int64BitsToDouble(reader.Long()),
            _ when schema.Size == 0 => Array.Empty<Value>(),
            _ => throw new NotSupportedException($"字段格式：{schema.Type}")
        };
        if (align) reader.Align();
        return new(schema, data);
    }
}
