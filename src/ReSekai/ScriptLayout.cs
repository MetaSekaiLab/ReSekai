using System.Reflection;
using ReSekai.Decompiler.Model;

namespace ReSekai;

readonly record struct ScriptName(string Assembly, string Namespace, string Name)
{
    public static string AssemblyName(string name) => name.EndsWith(".dll", StringComparison.Ordinal) ? name[..^4] : name;
    public string Key => $"{AssemblyName(Assembly)}:{Namespace}:{Name}";
    public override string ToString() => Key;
}

static class Attributes
{
    public static bool HasAttribute(this IReadOnlyList<CustomAttributeDef> attributes, string name) => attributes.Any(a => a.Constructor.Owner.FullName == name);
}

sealed class ScriptLayout(ProgramModel model)
{
    readonly SerializedFieldNames fieldNames = new(model);
    readonly Dictionary<string, TypeDef> types = model.Types.Where(t => t.DeclaringType is null)
        .ToDictionary(t => new ScriptName(t.Assembly, t.Namespace, t.Name).Key);
    readonly Dictionary<ScriptName, Schema[]> cache = [];
    static readonly Dictionary<string, (string Type, int Size)> primitives = new()
    {
        ["Boolean"] = ("UInt8", 1), ["Byte"] = ("UInt8", 1), ["SByte"] = ("SInt8", 1), ["Char"] = ("UInt16", 2),
        ["Int16"] = ("SInt16", 2), ["UInt16"] = ("UInt16", 2), ["Int32"] = ("int", 4), ["UInt32"] = ("unsigned int", 4),
        ["Int64"] = ("SInt64", 8), ["UInt64"] = ("UInt64", 8), ["Single"] = ("float", 4), ["Double"] = ("double", 8)
    };
    static readonly Dictionary<string, string> nativeNames = new()
    {
        ["Vector2"] = "Vector2f", ["Vector3"] = "Vector3f", ["Vector4"] = "Vector4f", ["Quaternion"] = "Quaternionf",
        ["Color"] = "ColorRGBA", ["Rect"] = "Rectf", ["Bounds"] = "AABB", ["LayerMask"] = "BitField",
        ["Matrix4x4"] = "Matrix4x4f", ["Vector2Int"] = "int2_storage", ["Vector3Int"] = "int3_storage",
        ["AnimationCurve"] = "AnimationCurve", ["Gradient"] = "Gradient", ["RectOffset"] = "RectOffset", ["Hash128"] = "Hash128"
    };

    public Schema[] For(ScriptName script)
    {
        if (cache.TryGetValue(script, out var found)) return found;
        if (!types.TryGetValue(script.Key, out var type) && script.Assembly == "UnityEngine")
            type = model.Types.FirstOrDefault(t => t.Namespace == script.Namespace && t.Name == script.Name && t.Assembly.StartsWith("UnityEngine.", StringComparison.Ordinal));
        if (type is null) throw new InvalidDataException($"metadata 缺少类型 {script}");
        return cache[script] = Fields(new(type, []), []);
    }

    Schema[] Fields(DefinitionRef type, HashSet<DefinitionRef> ancestors)
    {
        if (type.Definition.FullName is "UnityEngine.Object" or "UnityEngine.Component" or "UnityEngine.Behaviour" or "UnityEngine.MonoBehaviour" or "UnityEngine.ScriptableObject" or "System.Object") return [];
        List<Schema> result = [];
        if (type.Definition.BaseType?.Substitute(type.Arguments, []) is DefinitionRef parent) result.AddRange(Fields(parent, ancestors));
        foreach (var field in type.Definition.Fields)
        {
            if ((field.Attributes & (FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.InitOnly | (FieldAttributes)128)) != 0) continue;
            bool reference = field.CustomAttributes.HasAttribute("UnityEngine.SerializeReference");
            if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public && !field.CustomAttributes.HasAttribute("UnityEngine.SerializeField") && !reference) continue;
            if (reference || field.CustomAttributes.HasAttribute("System.Runtime.CompilerServices.FixedBufferAttribute")) throw new NotSupportedException($"不支持的序列化字段：{type.Definition.FullName}.{field.Name}");
            if (Field(field.Type.Substitute(type.Arguments, []), fieldNames.Name(field), ancestors) is { } schema) result.Add(schema);
        }
        return result.ToArray();
    }

    Schema? Field(TypeRef type, string name, HashSet<DefinitionRef> ancestors, bool element = false)
    {
        TypeRef? item = type switch
        {
            ArrayRef { Rank: 1, IsVector: true } array => array.Element,
            DefinitionRef { Definition: { Namespace: "System.Collections.Generic", Name: "List`1" }, Arguments: [var argument] } => argument,
            _ => null
        };
        if (item is not null)
        {
            if (element || Field(item, "data", ancestors, true) is not { } child) return null;
            bool align = child.Integer || child.Type is "float" or "double" or "string" || nativeNames.Values.Contains(child.Type);
            return Schema.Array(name, child, align);
        }
        if (type is not DefinitionRef definition) return null;
        var value = definition.Definition;
        if (value.Namespace == "System" && primitives.TryGetValue(value.Name, out var primitive))
            return new(primitive.Type, name, primitive.Size, primitive.Size < 4 && !element ? 0x4000 : 0);
        if (value.FullName is "System.String" or "UnityEngine.PropertyName") return new("string", name, Flags: 0x4000);
        if (value.IsEnum) return Field(value.Fields.First(f => !f.IsStatic).Type, name, ancestors, element);
        if (Inherits(value, "UnityEngine.Object")) return new($"PPtr<{value.Name}>", name, 12, Fields: [new("int", "m_FileID", 4), new("SInt64", "m_PathID", 8)]);
        if (value.FullName == "UnityEngine.Color32") return new("ColorRGBA", name, 4, Version: 2, Fields: [new("unsigned int", "rgba", 4)]);
        if (value.FullName == "UnityEngine.RectOffset") return new("RectOffset", name, 16, Fields: new[] { "m_Left", "m_Right", "m_Top", "m_Bottom" }.Select(n => new Schema("int", n, 4)).ToArray());
        if (value.Namespace == "UnityEngine" && nativeNames.TryGetValue(value.Name, out var native))
        {
            if (!Schemas.Native.TryGetValue(native, out var layout)) throw new NotSupportedException($"缺少原生布局：{value.FullName}");
            return layout.Rename(name);
        }
        if (value.FullName is "UnityEngine.GUIStyle" or "UnityEngine.LazyLoadReference`1") throw new NotSupportedException($"尚未支持的字段类型：{value.FullName}");
        if (value.Namespace.StartsWith("System", StringComparison.Ordinal) || value.IsInterface || (value.Attributes & TypeAttributes.Abstract) != 0
            || value.GenericParameters.Count > definition.Arguments.Count || definition.Arguments.Any(a => a.ContainsGenericParameter())
            || value.CustomAttributes.HasAttribute("System.Runtime.CompilerServices.CompilerGeneratedAttribute") || Inherits(value, "System.MulticastDelegate")) return null;
        if ((value.Attributes & (TypeAttributes)0x2000) == 0 && !(value.Namespace == "UnityEngine" && value.Name == "ExposedReference`1")) return null;
        if (!ancestors.Add(definition)) return null;
        Schema[] fields = Fields(definition, ancestors);
        ancestors.Remove(definition);
        return new(value.Name, name, fields.Length == 0 ? 0 : -1, Fields: fields);
    }

    public static bool Inherits(TypeDef type, string name)
    {
        for (TypeDef? current = type; current is not null; current = (current.BaseType as DefinitionRef)?.Definition)
            if (current.FullName == name) return true;
        return false;
    }
}
