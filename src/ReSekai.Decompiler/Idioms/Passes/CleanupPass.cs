using ReSekai.Decompiler.Machine.Ssa;
using static ReSekai.Decompiler.Idioms.IdiomRewriter;

namespace ReSekai.Decompiler.Idioms;

internal static class CleanupPass
{
    internal static void Cleanup(this IdiomState state)
    {
        state.Result.Errors.AddRange(Lowering.Cleanup(state.function));
        // 规则看到的表达式不含私有栈槽的搬运：转发后再清理一次。
        while (ReSekai.Decompiler.Machine.Storage.FrameForwarding.Run(state.function, state.owner.CallEffects))
            state.Result.Errors.AddRange(Lowering.Cleanup(state.function));
        state.removed.Clear();
        state.defs = new Defs(state.function);
    }
}
