using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

/// <summary>
/// 一臂只有 φ 与空语句、直接跳到另一臂的分支：条件不读内存时求值没有副作用，两条路到达汇合块时各 φ 取同一个值，分支等于直接跳到汇合块。
/// 尾合并后残留的、初始化已删除的守卫（只剩 beforefieldinit 的空语句）是这种形态。
/// </summary>
internal static class EmptyArmBranches
{
    public static bool Run(MachineFunction function, Func<Expr, Expr>? expand = null)
    {
        var changed = false;
        foreach (var head in function.Blocks)
        {
            if (head.Statements.LastOrDefault() is not Branch branch || head.Handler is not null
                || ExprMap.Walk(branch.Condition).OfType<Load>().Any(l => !IsBeforeFieldInitFlag(l))) continue;
            var normal = head.NormalSuccessors.ToArray();
            if (normal.Length != 2 || normal[0] == normal[1]) continue;
            for (var side = 0; side < 2; side++)
            {
                var (arm, join) = (normal[side], normal[1 - side]);
                if (arm.Handler is not null || arm.NormalSuccessors.ToArray() is not [var next] || next != join
                    || (!arm.Statements.All(s => s is Phi or Nop or Jump) && (expand is null || !RepeatedBounds(head, arm, branch, expand)))) continue;
                var (fromHead, armEdge, headEdge) = (arm.Predecessors.IndexOf(head), join.Predecessors.IndexOf(arm), join.Predecessors.IndexOf(head));
                if (fromHead < 0 || armEdge < 0 || headEdge < 0 || arm.Predecessors.Count(p => p == head) != 1
                    || join.Predecessors.Count(p => p == arm) != 1 || join.Predecessors.Count(p => p == head) != 1) continue;
                var passed = arm.Statements.OfType<Phi>().ToDictionary(p => p.Target, p => p.Sources[fromHead]);
                bool Same(Phi phi) => (passed.TryGetValue(phi.Sources[armEdge], out var source) ? source : phi.Sources[armEdge]).Equals(phi.Sources[headEdge]);
                if (!join.Statements.OfType<Phi>().All(Same)) continue;
                head.Statements[^1] = new Jump(branch.Address, join.Start);
                MachineFunction.RemoveEdge(head, arm);
                changed = true;
                break;
            }
        }
        return changed;
    }

    // 初始化慢路上重复的数组长度读取与边界检查，已被必经入边的同一检查覆盖。
    private static bool RepeatedBounds(Block head, Block arm, Branch branch, Func<Expr, Expr> expand)
    {
        var condition = expand(branch.Condition);
        if (ExprMap.Walk(condition).OfType<Load>().ToArray() is not [var flag] || !IsBeforeFieldInitFlag(flag)
            || arm.Statements.OfType<Call>().ToArray() is not [Call { Op.Kind: OpKind.BoundsCheck, Arguments: [var index, var length] }]
            || expand(length) is not Load { Address: Binary { Op: BinOp.Add, Left: Use, Right: Const { Value: RuntimeLayout.ArrayMaxLength } } } size)
            return false;
        if (arm.Statements.Any(s => s is not (Nop or Jump or Call)
            && (s is not Define d || ExprMap.Walk(expand(d.Value)).Any(e => e is Intrinsic or Unknown || e is Load l && l != size))))
            return false;
        return head.Predecessors is [var predecessor] && predecessor.NormalSuccessors.SequenceEqual([head])
            && predecessor.Predecessors is [var checkedBlock] && checkedBlock.NormalSuccessors.SequenceEqual([predecessor])
            && predecessor.Statements.All(s => s is Nop or Jump or Call { Op.Kind: OpKind.NullCheck })
            && checkedBlock.Statements.LastOrDefault(s => s is not Jump) is Call { Op.Kind: OpKind.BoundsCheck, Arguments: [var checkedIndex, var checkedLength] }
            && expand(index) == expand(checkedIndex) && size == expand(checkedLength);
    }

    internal static bool IsBeforeFieldInitFlag(Load load) => load is
        {
            Width: 32, Address: Binary
            {
                Op: BinOp.Add, Left: Symbol { Kind: SymbolKind.Class, Value: DefinitionRef type },
                Right: Const { Value: RuntimeLayout.ClassCctorFinishedOrNoCctor },
            },
        } && (type.Definition.Attributes & System.Reflection.TypeAttributes.BeforeFieldInit) != 0;
}
