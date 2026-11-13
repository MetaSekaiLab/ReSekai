using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

/// <summary>Android ARM64 的 TLS 栈保护配对；保存值已由帧提升证明未被改写。</summary>
internal static class StackGuardNormalizer
{
    internal static bool Run(MachineFunction function)
    {
        var definitions = new IdiomRewriter.Defs(function);
        var phis = function.Blocks.SelectMany(b => b.Statements).OfType<Phi>().ToDictionary(phi => phi.Target);
        // Bionic TLS_SLOT_STACK_GUARD = 5，每槽 8 字节。
        static bool Guard(Expr expression) => expression is Load
        {
            Width: 64,
            Address: Binary
            {
                Op: BinOp.Add, Left: Intrinsic { Name: "sysreg_tpidr_el0", Arguments.Length: 0 },
                Right: Const { Value: 40 }
            }
        };
        // 尾部合并可以先从同一个 TLS 槽分别读取，再合流到同一比较。
        bool AllGuardReads(Expr operand, int remaining = 8)
        {
            var expanded = definitions.Expand(operand);
            if (Guard(expanded)) return true;
            return remaining > 0 && expanded is Use { Width: 64 } use
                && phis.TryGetValue(use.Variable, out var phi) && phi.Sources.Length > 0
                && phi.Sources.All(source => AllGuardReads(new Use(source, 64), remaining - 1));
        }

        var changed = false;
        foreach (var block in function.Blocks)
        {
            if (block.Statements.LastOrDefault() is not Branch
                { Condition: Compare { Cond: Cond.Eq or Cond.Ne } comparison } branch
                || !AllGuardReads(comparison.Left) || !AllGuardReads(comparison.Right)) continue;
            var failureAddress = comparison.Cond == Cond.Ne ? branch.Target : branch.Fallthrough;
            var failure = block.NormalSuccessors.FirstOrDefault(b => b.Start == failureAddress);
            // 失败一侧可能先经只有跳转的块（尾部合并后的落地块）。
            for (var hops = 0; hops < 4 && failure is { Statements: [Jump], NormalSuccessors: var next } && next.Count() == 1; hops++)
                failure = next.First();
            if (failure is null || failure.Statements.OfType<Call>().ToArray() is not
                [{ Op: { Kind: OpKind.Import, Operand: "__stack_chk_fail" } }]
                || failure.Statements.Any(s => s is not (Define or Nop or Call))) continue;
            var successors = block.NormalSuccessors.ToArray();
            if (successors.Length != 2 || successors[0].Start != branch.Target || successors[1].Start != branch.Fallthrough) continue;
            block.Statements[^1] = new Jump(branch.Address, comparison.Cond == Cond.Ne ? branch.Fallthrough : branch.Target);
            MachineFunction.RemoveEdgeAt(block, comparison.Cond == Cond.Ne ? 0 : 1);
            changed = true;
        }
        return changed;
    }
}
