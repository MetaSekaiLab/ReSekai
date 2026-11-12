using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

internal static class TrimResultBuffersPass
{
    internal static void TrimResultBuffers(this IdiomState state)
    {
        if (state.owner.NativeCallees.Convention.IndirectResult is not { } buffer)
            return;
        var changed = false;
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not Call { Op: null, Kind: CallKind.Direct or CallKind.Tail, Target: Const target } call || state.facts.Code.RegionOf(target.Value) != CodeRegion.Managed || state.owner.calls.Signature(target.Value) is not null)
                    continue;
                var index = Array.IndexOf(call.Sources, buffer);
                if (index < 0 || !state.owner.UnusedResultBuffer(target.Value))
                    continue;
                block.Statements[i] = call with
                {
                    Sources = call.Sources.Where((_, k) => k != index).ToArray(),
                    Arguments = call.Arguments.Where((_, k) => k != index).ToArray(),
                };
                changed = true;
            }

        if (changed)
            state.Cleanup();
    }
}
