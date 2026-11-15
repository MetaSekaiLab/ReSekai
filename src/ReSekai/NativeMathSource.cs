using System.Text;
using Mono.Cecil;

namespace ReSekai;

static class NativeMathSource
{
    public const string Path = "ReSekai.Decompiler/NativeImports.cs";

    public static string ForUnity(string source, string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        var type = module.GetType("ReSekai.Decompiler.NativeImports");
        if (type is null || type.Methods.Count == 0
            || type.Methods.Any(m => m.PInvokeInfo?.Module.Name != "libm.so")) return source;

        var code = new StringBuilder("#if UNITY_ANDROID && !UNITY_EDITOR\n")
            .Append(source.TrimEnd()).AppendLine()
            .AppendLine("#else")
            .AppendLine("namespace ReSekai.Decompiler")
            .AppendLine("{")
            .AppendLine("    public static class NativeImports")
            .AppendLine("    {");
        foreach (var method in type.Methods)
        {
            var body = DesktopBody(method);
            if (body is null) return source;
            code.Append("        public static ").Append(TypeName(method.ReturnType)).Append(' ').Append(method.Name)
                .Append('(').AppendJoin(", ", method.Parameters.Select(p => TypeName(p.ParameterType) + " " + p.Name))
                .Append(')').AppendLine(body);
        }
        return code.AppendLine("    }").AppendLine("}").AppendLine("#endif").ToString();
    }

    static string TypeName(TypeReference type) => type switch
    {
        ByReferenceType byRef => "ref " + TypeName(byRef.ElementType),
        { MetadataType: MetadataType.Void } => "void",
        { MetadataType: MetadataType.Single } => "float",
        { MetadataType: MetadataType.Double } => "double",
        { MetadataType: MetadataType.Int32 } => "int",
        _ => throw new InvalidDataException($"未支持的 libm 参数类型 {type.FullName}"),
    };

    static string? DesktopBody(MethodDefinition method)
    {
        var symbol = method.PInvokeInfo.EntryPoint;
        if (symbol is "sincos" or "sincosf")
        {
            string math = symbol == "sincosf" ? "MathF" : "Math";
            return $" {{ sine = global::System.{math}.Sin(value); cosine = global::System.{math}.Cos(value); }}";
        }
        bool single = method.ReturnType.MetadataType == MetadataType.Single;
        string name = single ? symbol[..^1] : symbol;
        string? standard = name switch
        {
            "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "atan2" or "exp" or "log" or "log10" or "sqrt" or "floor" or "pow"
                => char.ToUpperInvariant(name[0]) + name[1..],
            "fabs" => "Abs",
            "ceil" => "Ceiling",
            "trunc" => "Truncate",
            _ => null,
        };
        string library = single ? "global::System.MathF" : "global::System.Math";
        if (standard is not null)
            return $" => {library}.{standard}({string.Join(", ", method.Parameters.Select(p => p.Name))});";
        return name switch
        {
            "fmod" => " => value % divisor;",
            "exp2" => $" => {library}.Pow(2, value);",
            "log2" => $" => {library}.Log(value, 2);",
            "round" => $" => {library}.Round(value, global::System.MidpointRounding.AwayFromZero);",
            // 对 float，超出 ±512 的倍率只可能使非零有限值上溢或下溢。double 中间值保留次正规数。
            "ldexp" or "scalbn" when single => " => (float)(value * global::System.Math.Pow(2, global::System.Math.Max(-512, global::System.Math.Min(512, exponent))));",
            _ => null,
        };
    }
}
