using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Ssa;
using static ReSekai.Decompiler.Idioms.IdiomRewriter;

namespace ReSekai.Decompiler.Idioms;

internal static class ReplaceLazyPass
{
    internal static void ReplaceLazy(this IdiomState state)
    {
        var calls = new Dictionary<Call, Symbol>(ReferenceEqualityComparer.Instance);
        foreach (var (call, symbol) in state.lazyValues)
            calls[call] = symbol;
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not Call call || !calls.TryGetValue(call, out var value))
                    continue;
                if (call.Kind == CallKind.Tail)
                    block.Statements[i] = new Return(call.Address) { Pieces = [new(0, value)] };
                else if (call.Defines.FirstOrDefault() is { } result)
                    block.Statements[i] = new Define(call.Address, result, value);
                else
                    block.Statements[i] = new Nop(call.Address, "InitMetadata");
            }

        state.Result.Errors.AddRange(Lowering.Cleanup(state.function));
        state.defs = new Defs(state.function);
    }
}
