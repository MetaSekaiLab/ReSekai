using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

internal static class RemovePass
{
    internal static void Remove(this IdiomState state, IEnumerable<Block> blocks) => state.removed.UnionWith(blocks);
}
