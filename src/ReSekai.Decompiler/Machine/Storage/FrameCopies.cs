using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Machine.Storage;

/// <summary>
/// 帧内的整段复制（memcpy）链：结构体局部经几个临时副本传递时，后一次复制改从最初的来源取，来源与目标相同的复制没有作用，
/// 不再被观察的副本随之删除。只沿唯一前驱往回找，途中两段内存都不能被改写。
/// </summary>
internal static class FrameCopies
{
    private const int Rounds = 8;
    private sealed record Copy(long Target, long Source, int Size);

    public static bool Run(MachineFunction function, IFrameCallEffects calls)
    {
        if (function.Blocks.SelectMany(b => b.Statements).Count(s => Of(s) is not null) < 2) return false;
        var any = false;
        for (var round = 0; round < Rounds; round++)
        {
            var model = new FrameModel(function, calls);
            if (!model.HasPointerFacts) return any;
            var used = function.Blocks.SelectMany(b => b.Statements).SelectMany(Inputs).ToHashSet();
            var changed = false;
            foreach (var block in function.Blocks)
                for (var i = 0; i < block.Statements.Count && !changed; i++)
                {
                    if (Of(block.Statements[i]) is not { } copy || block.Statements[i] is not Call call || call.Defines.Any(d => d is not null && used.Contains(d))) continue;
                    if (copy.Target == copy.Source || model.Unobserved(copy.Target, copy.Size, call.Site))
                    {
                        block.Statements[i] = new Nop(call.Address, "帧内复制");
                        changed = true;
                    }
                    else if (Origin(model, block, i, copy) is { } origin && origin != copy.Source)
                    {
                        block.Statements[i] = call with { Arguments = [call.Arguments[0], new FrameAddress(origin), .. call.Arguments.Skip(2)] };
                        changed = true;
                    }
                }
            if (!changed) break;
            any = true;
        }
        return any;
    }

    private static Copy? Of(Stmt statement) => statement is Call { Op: { Kind: OpKind.Import, Operand: "memcpy" or "memmove" },
        Arguments: [FrameAddress target, FrameAddress source, Const { Value: > 0 and <= 4096 } size] }
        ? new(target.Offset, source.Offset, (int)size.Value) : null;

    private static IEnumerable<Variable> Inputs(Stmt statement) => statement is Phi phi ? phi.Sources
        : ExprMap.Operands(statement).SelectMany(ExprMap.Walk).OfType<Use>().Select(u => u.Variable);

    private static long? Origin(FrameModel model, Block block, int index, Copy copy)
    {
        var path = new List<Block> { block };
        for (var i = index - 1; ; i--)
        {
            while (i < 0)
            {
                if (block.IsLandingPad || block.Predecessors is not [var previous] || path.Contains(previous)) return null;
                path.Add(previous);
                (block, i) = (previous, previous.Statements.Count - 1);
            }
            var statement = block.Statements[i];
            if (Of(statement) is { } earlier && earlier.Target <= copy.Source && copy.Source + copy.Size <= earlier.Target + earlier.Size)
            {
                var source = earlier.Source + (copy.Source - earlier.Target);
                // 来源在两次复制之间也不能变。
                return Unchanged(model, i + 1, index, path, source, copy.Size) ? source : null;
            }
            if (model.MayWrite(statement, copy.Source, copy.Size)) return null;
        }
    }

    /// <summary>从 from 起沿唯一后继到达后一次复制之前，这段内存没有被改写。</summary>
    private static bool Unchanged(FrameModel model, int from, int end, List<Block> path, long offset, int size)
    {
        // path 从后往前收集，最先加入的是后一次复制所在的块。
        for (var at = path.Count - 1; at >= 0; at--)
        {
            var statements = path[at].Statements;
            var stop = at == 0 ? end : statements.Count;
            for (var i = at == path.Count - 1 ? from : 0; i < stop; i++)
                if (model.MayWrite(statements[i], offset, size)) return false;
        }
        return true;
    }
}
