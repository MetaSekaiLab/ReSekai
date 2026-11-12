using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

internal static class ReachingAtPass
{
    internal static Variable? ReachingAt(this IdiomState state, Block block, Storage storage, Dictionary<Block, Variable?> memo) => state.Reaching(block, storage, memo, [], out _) is var (v, known) && known ? v : null;
}
