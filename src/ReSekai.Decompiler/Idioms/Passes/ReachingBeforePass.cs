using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Ir;
using static ReSekai.Decompiler.Idioms.IdiomState;

namespace ReSekai.Decompiler.Idioms;

internal static class ReachingBeforePass
{
    internal static Variable? ReachingBefore(this IdiomState state, Block head, Point start, Storage storage) => state.LastDefinition(head, storage, start.Index, start.Address) ?? state.ReachingAt(head, storage, new());
}
