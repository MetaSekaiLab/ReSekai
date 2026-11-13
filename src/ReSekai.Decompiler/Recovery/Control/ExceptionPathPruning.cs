using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Ssa;

namespace ReSekai.Decompiler.Recovery.Control;

/// <summary>异常清理合流时，沿同一入边解释退出原因与待抛异常，折叠不可能到达的退出分支。</summary>
internal static class ExceptionPathPruning
{
    public static bool Run(MachineFunction function)
    {
        if (!function.Blocks.Any(b => b.IsLandingPad)) return false;
        var statements = function.Blocks.SelectMany(b => b.Statements).ToArray();
        var definitions = statements.OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
        var wrappers = statements.OfType<Call>().Where(c => c.Op?.Kind == OpKind.BeginCatch)
            .SelectMany(c => c.Defines.Take(1)).OfType<Variable>().ToHashSet();
        if (wrappers.Count == 0) return false;
        var merged = statements.OfType<Phi>().Select(p => p.Target).ToHashSet();
        bool Caught(Expr value) => value is Load { Width: 64, Address: Use { Width: 64 } pointer } && wrappers.Contains(pointer.Variable);
        Expr Expand(Expr expression, int depth = 0) => depth >= 10 ? expression : Fold.Simplify(ExprMap.Map(expression, part => part switch
        {
            Use use when definitions.TryGetValue(use.Variable, out var value) && value is not Intrinsic
                && (Caught(value) || !ExprMap.Walk(value).Any(e => e is Load or Unknown or Intrinsic))
                => Fold.Extract(Expand(value, depth + 1), 0, use.Width),
            _ => null,
        }));
        Expr Condition(Expr expression)
        {
            var expanded = Expand(expression);
            return Fold.Simplify(ExprMap.Map(expanded, part => part is Compare { Cond: Cond.Eq or Cond.Ne, Left: var left, Right: Const { Value: 0 } } check
                && Caught(left) ? Expr.C(check.Cond == Cond.Ne ? 1UL : 0UL, 1) : null));
        }

        foreach (var block in function.Blocks)
        {
            if (block.Statements.LastOrDefault() is not Branch branch || branch.Target == branch.Fallthrough
                || Condition(branch.Condition) is not Compare { Cond: Cond.Eq or Cond.Ne, Left: Use selector, Right: Const { Value: 0 } }
                || !merged.Contains(selector.Variable)) continue;
            var active = new HashSet<Block>();
            var budget = 384;
            var dead = Impossible(block, [branch.Condition]) ? branch.Target
                : Impossible(block, [new Not(branch.Condition)]) ? branch.Fallthrough : (ulong?)null;
            if (dead is null || block.NormalSuccessors.SingleOrDefault(b => b.Start == dead) is not { } unreachable) continue;
            block.Statements[^1] = new Jump(branch.Address, dead == branch.Target ? branch.Fallthrough : branch.Target);
            MachineFunction.RemoveEdgeAt(block, block.Successors.IndexOf(unreachable));
            return true;

            bool Impossible(Block current, IReadOnlyList<Expr> required)
            {
                RecoveryCancellation.Check();
                if (--budget < 0) return false;
                var remaining = required.Select(Condition).Distinct().ToArray();
                if (remaining.Any(c => c is Const { Value: 0 } || remaining.Contains(Fold.Simplify(new Not(c))))) return true;
                if (current == function.EntryBlock || !active.Add(current)) return false;
                var unresolved = remaining.Where(c => c is not Const).ToArray();
                var phis = current.Statements.OfType<Phi>().ToDictionary(p => p.Target);
                var impossible = unresolved.Length > 0 && current.Predecessors.Count > 0
                    && current.Predecessors.Select((previous, edge) => (previous, edge)).All(incoming =>
                    {
                        var (previous, edge) = incoming;
                        var needs = unresolved.Select(condition => ExprMap.Map(condition, part => part is Use use
                            && phis.TryGetValue(use.Variable, out var phi) && phi.Sources[edge] is { } source
                            ? use with { Variable = source } : null)).ToList();
                        if (previous.Handler != current && previous.Statements.LastOrDefault() is Branch guard && guard.Target != guard.Fallthrough)
                            needs.Add(current.Start == guard.Target ? guard.Condition : new Not(guard.Condition));
                        return Impossible(previous, needs);
                    });
                active.Remove(current);
                return impossible;
            }
        }
        return false;
    }
}
