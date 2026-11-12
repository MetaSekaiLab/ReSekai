using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

internal static class ClassOperandPass
{
    internal static object? ClassOperand(this IdiomState state, IEnumerable<Expr> arguments) => arguments.Select(a => state.defs.Expand(a)).OfType<Symbol>().FirstOrDefault(s => s.Kind == SymbolKind.Class)?.Value;
}
