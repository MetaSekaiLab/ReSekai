using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

internal static class RawDefinitionPass
{
    internal static Expr? RawDefinition(this IdiomState state, Expr e) => e is Use { Variable: var v } ? state.defs.Raw(v) : null;
}
