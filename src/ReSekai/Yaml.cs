using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ReSekai;

sealed class Yaml(Func<Value?, string> reference)
{
    public sealed record Literal(string Text);
    public static string Mapping(string name, Dictionary<string, object?> fields)
    {
        StringBuilder result = new(name + ":\n");
        void Write(object? value, int indent, string lead)
        {
            string pad = new(' ', indent);
            if (value is Dictionary<string, object?> map && map.Count > 0)
            {
                if (lead == "-")
                {
                    bool first = true;
                    foreach (var pair in map)
                    {
                        Write(pair.Value, first ? indent : indent + 2, (first ? "- " : "") + (RegexKey(pair.Key) ? pair.Key : Quote(pair.Key)) + ":");
                        first = false;
                    }
                    return;
                }
                if (lead.Length > 0) result.Append(pad).Append(lead).AppendLine();
                foreach (var pair in map)
                    Write(pair.Value, indent + (lead.Length == 0 ? 0 : lead.StartsWith("- ", StringComparison.Ordinal) ? 4 : 2), (RegexKey(pair.Key) ? pair.Key : Quote(pair.Key)) + ":");
            }
            else if (value is object[] array && array.Length > 0)
            {
                result.Append(pad).Append(lead).AppendLine();
                foreach (var item in array) Write(item, indent, "-");
            }
            else
            {
                string scalar = value switch { Literal raw => raw.Text, string s => Quote(s), object[] => "[]", Dictionary<string, object?> or null => "{}", _ => Number(value) };
                result.Append(pad).Append(lead).Append(' ').AppendLine(scalar);
            }
        }
        Write(fields, 2, "");
        return result.ToString();
        static bool RegexKey(string key) => key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
    readonly StringBuilder text = new("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");
    public void Document(int classId, long id, Schema schema, Value value, bool settings = false)
    {
        text.Append($"--- !u!{classId} &{id}\n{schema.Type}:\n");
        Body(schema, value, 2, "", settings);
    }
    public override string ToString() => text.ToString();
    public static string Quote(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    void Line(int indent, string prefix, string content) => text.Append(' ', indent).Append(prefix).Append(content).Append('\n');

    void Body(Schema schema, Value? value, int indent, string prefix, bool omit = false)
    {
        if (schema.Version > 1) { Line(indent, prefix, $"serializedVersion: {schema.Version}"); indent += prefix.Length; prefix = ""; }
        if (schema.Type == "Hash128") { Line(indent, prefix, "Hash: " + Hash(value)); return; }
        Dictionary<string, int> counts = [];
        foreach (Schema field in schema.Children)
        {
            int occurrence = counts.GetValueOrDefault(field.Name);
            counts[field.Name] = occurrence + 1;
            Value? child = value?.Items.Where(v => v.Schema.Name == field.Name).Skip(occurrence).FirstOrDefault();
            if (omit && child is null) continue;
            Field(field.Name, field, child, indent, prefix, schema.Type == "ExposedReferenceTable");
            indent += prefix.Length;
            prefix = "";
        }
    }

    void Field(string key, Schema schema, Value? value, int indent, string prefix = "", bool sequence = false)
    {
        string name = key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c == '_') ? key : Quote(key);
        if (schema.Type == "TypelessData")
        {
            byte[] bytes = value?.Bytes ?? [];
            Line(indent, prefix, name + ": " + bytes.Length);
            Line(indent + prefix.Length, "", "_typelessdata: " + Convert.ToHexStringLower(bytes));
        }
        else if (Scalar(schema, value) is { } scalar) Line(indent, prefix, name + ": " + (sequence && scalar == "{}" ? "[]" : scalar));
        else
        {
            Line(indent, prefix, name + ":");
            int next = indent + prefix.Length;
            if (schema.Element is { } element)
            {
                foreach (var item in value?.Items ?? [])
                {
                    if (schema.Type == "map" && !sequence && Key(element.Children[0])) Field(KeyText(item.Items[0]), element.Children[1], item.Items[1], next + 2);
                    else Item(element, item, next);
                }
            }
            else Body(schema, value, next + 2, "");
        }
    }

    void Item(Schema schema, Value value, int indent)
    {
        if (Scalar(schema, value) is { } scalar) Line(indent, "- ", scalar);
        else if (schema.Type == "pair" && Key(schema.Children[0])) Field(KeyText(value.Items[0]), schema.Children[1], value.Items[1], indent, "- ");
        else Body(schema, value, indent, "- ");
    }

    string? Scalar(Schema schema, Value? value)
    {
        if (schema.Pointer) return reference(value);
        if (schema.Type == "string") return Quote(value?.Text ?? "");
        if (schema.Type == "GUID") return Guid(value);
        if (schema.Element is { } element)
        {
            if (element.Integer) return Convert.ToHexStringLower(value?.Bytes ?? []);
            if (value?.Items.Length is null or 0) return schema.Type == "map" ? "{}" : "[]";
            return null;
        }
        if (schema.Children.Length == 0) return schema.Integer || schema.Type is "float" or "double" ? Number(value?.Data) : "{}";
        if ((schema.Flags & 0x200000) != 0)
        {
            var fields = schema.Children.Select(c => c.Name + ": " + (Scalar(c, value?.Find(c.Name)) ?? throw new InvalidDataException($"不能内联字段：{c.Name}")));
            if (schema.Version > 1) fields = new[] { "serializedVersion: " + schema.Version }.Concat(fields);
            return "{" + string.Join(", ", fields) + "}";
        }
        return null;
    }
    static bool Key(Schema schema) => schema.Integer || schema.Type is "string" or "GUID";
    static string KeyText(Value value) => value.Schema.Type == "string" ? value.Text : value.Schema.Type == "GUID" ? Guid(value) : Number(value.Data);
    static string Number(object? number) => number switch
    {
        null => "0", bool flag => flag ? "1" : "0",
        float f when float.IsNaN(f) => ".nan", float f when float.IsPositiveInfinity(f) => ".inf", float f when float.IsNegativeInfinity(f) => "-.inf",
        double d when double.IsNaN(d) => ".nan", double d when double.IsPositiveInfinity(d) => ".inf", double d when double.IsNegativeInfinity(d) => "-.inf",
        float f => f.ToString("R", CultureInfo.InvariantCulture), double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture), _ => throw new InvalidDataException("无效数值")
    };
    public static string Guid(Value? value) => value is null ? new('0', 32)
        : string.Concat(value.Items.Select(v => new string(Convert.ToUInt32(v.Data).ToString("x8").Reverse().ToArray())));
    static string Hash(Value? value)
    {
        if (value is null) return new('0', 32);
        byte[] bytes = value.Items.Select(v => checked((byte)v.Number)).ToArray();
        return string.Concat(Enumerable.Range(0, 4).Select(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)).ToString("x8")));
    }
}
