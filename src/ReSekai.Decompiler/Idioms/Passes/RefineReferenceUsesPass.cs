using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Ssa;

namespace ReSekai.Decompiler.Idioms;

internal static class RefineReferenceUsesPass
{
    internal static void RefineReferenceUses(this IdiomState state, Block start, int position, Use obj, Variable cast, Dominators dominators) => CheckedClassValues.Refine(state.function, start, position, obj, cast, dominators);
}
