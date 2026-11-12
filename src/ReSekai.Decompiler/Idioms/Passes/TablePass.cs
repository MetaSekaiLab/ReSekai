using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

internal static class TablePass
{
    internal static RgctxTable Table(MethodRef method) => new(method.Method.Owner.Assembly, method.Method.Token, method.ToString(), method.TypeArguments, method.MethodArguments);
    internal static RgctxTable Table(DefinitionRef type) => new(type.Definition.Assembly, type.Definition.Token, type.ToString(), type.Arguments, []);
}
