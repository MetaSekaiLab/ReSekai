using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Algebra;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

internal static class FixedFrameIndexLoopsPass
{
    // 两次迭代分别固定下标和分支时，按原有顺序执行两份循环体，供帧内存分析区分两个访问位置。
    internal static bool FixedFrameIndexLoops(this IdiomState state)
    {
        var function = state.function;
        if (function.Range.ExceptionRegions.Length != 0) return false;
        foreach (var head in function.Blocks)
        {
            if (head == function.EntryBlock || head.Successors is not [var body] || body == head || body.Predecessors is not [var only] || only != head
                || body.Successors.Count != 2 || head.Predecessors.Count != 2 || head.Predecessors.Count(p => p == body) != 1
                || head.Handler is not null || body.Handler is not null || head.IsLandingPad || body.IsLandingPad
                || body.Statements.LastOrDefault() is not Branch branch || body.Successors.Count(s => s == head) != 1
                || branch.Target != body.Successors[0].Start || branch.Fallthrough != body.Successors[1].Start) continue;
            var entry = head.Predecessors.Single(p => p != body);
            var exit = body.Successors.Single(s => s != head);
            if (entry == head || entry == exit || entry.Successors is not [var next] || next != head || entry.Handler is not null
                || entry.Statements.LastOrDefault() is Branch or Return || head.Statements.Count + body.Statements.Count > 64
                || head.Statements.Concat(body.Statements.SkipLast(1)).Any(s => s is not (Phi or Define or Store or Call or Nop or Jump))
                || body.Statements.Any(s => s is Phi)) continue;
            var phis = head.Statements.OfType<Phi>().ToArray();
            if (phis.Length == 0 || phis.Any(p => p.Sources.Length != 2)) continue;
            var entryIndex = head.Predecessors.IndexOf(entry);
            var backIndex = 1 - entryIndex;
            var backIsTaken = body.Successors[0] == head;
            if (Outcome(entryIndex) != backIsTaken || Outcome(backIndex) != !backIsTaken) continue;
            if (!head.Statements.Concat(body.Statements).SelectMany(Addresses).Any(address =>
                    state.defs.Expand(address) is Binary { Op: BinOp.Add, Left: FrameAddress, Right: var index }
                    && ExprMap.Walk(index).OfType<Use>().Any(use => phis.Any(p => p.Target == use.Variable
                        && Constant(p, entryIndex) && Constant(p, backIndex))))) continue;

            var label = Math.Max(NameSupply.JoinedEdgeBase, function.Blocks.Max(b => b.Start) + 1);
            var first = new Block(label) { End = label + 1 };
            var second = new Block(label + 1) { End = label + 2 };
            var path = new PathCopy(v => state.Fresh(v.Width), phis.ToDictionary(p => p.Target, p => p.Sources[entryIndex]));
            first.Statements = [.. head.Statements.Where(s => s is not (Phi or Jump)).Select(path.Copy), new Jump(head.End, second.Start)];
            second.Statements = [.. body.Statements.SkipLast(1).Select(path.Copy), new Jump(branch.Address, head.Start)];
            entry.Statements.RemoveAll(s => s is Jump);
            entry.Statements.Add(new Jump(entry.End, first.Start));
            entry.Successors[0] = first;
            first.Predecessors.Add(entry);
            MachineFunction.AddEdge(first, second);
            second.Successors.Add(head);
            head.Predecessors[entryIndex] = second;
            for (var i = 0; i < phis.Length; i++)
            {
                var source = phis[i].Sources[backIndex];
                var inputs = phis[i].Sources.ToArray();
                inputs[entryIndex] = path.Map.GetValueOrDefault(source, source);
                head.Statements[i] = phis[i] with { Sources = inputs };
            }
            MachineFunction.RemoveEdge(body, head);
            body.Statements[^1] = new Jump(branch.Address, exit.Start);
            function.Blocks.AddRange([first, second]);
            function.Renumber();
            return true;

            bool? Outcome(int edge)
            {
                var constants = phis.Where(p => Constant(p, edge)).ToDictionary(p => p.Target,
                    p => state.defs.Expand(new Use(p.Sources[edge], p.Target.Width)));
                var condition = Fold.Simplify(ExprMap.Map(state.defs.Expand(branch.Condition), e => e is Use use
                    && constants.TryGetValue(use.Variable, out var value) ? Fold.Extract(value, 0, use.Width) : null));
                return condition is Const c ? c.Value != 0 : null;
            }

            bool Constant(Phi phi, int edge) => state.defs.Expand(new Use(phi.Sources[edge], phi.Target.Width)) is Const;
        }
        return false;
    }

    private static IEnumerable<Expr> Addresses(Stmt statement) =>
        ExprMap.Operands(statement).SelectMany(ExprMap.Walk).OfType<Load>().Select(load => load.Address)
            .Concat(statement is Store store ? [store.Target] : []);
}
