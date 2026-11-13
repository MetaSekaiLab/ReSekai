using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Ssa;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

/// <summary>把两臂末尾的同类型分配合并到共同构造处，保留分支选出的构造参数。</summary>
internal static class MergedAllocationNormalizer
{
    private sealed record Path(Block Source, Call Allocation, IReadOnlyList<Stmt> Middle);

    public static bool Run(MachineFunction function, ProgramFacts facts, ManagedAbi abi, Func<int, Variable> fresh)
    {
        if (SelectConstantArguments(function)) return true;
        if (SplitConstructorChoice(function, facts, fresh)) return true;
        var changed = false;
        var dominators = Dominators.Compute(function);
        foreach (var block in function.Blocks)
            foreach (var phi in block.Statements.OfType<Phi>().ToArray())
            {
                if (!block.Statements.Contains(phi) || phi.Sources.Length < 2 || phi.Sources.Length != block.Predecessors.Count) continue;
                var prefix = new List<Stmt>();
                var destination = block;
                Call? constructor;
                // 异常区间可能把参数合流和构造调用切在两个基本块里。
                while (true)
                {
                    constructor = destination.Statements.OfType<Call>().FirstOrDefault();
                    prefix.AddRange(destination.Statements.TakeWhile(s => !ReferenceEquals(s, constructor))
                        .Where(s => s is not (Phi or Jump)));
                    if (constructor is not null || destination.NormalSuccessors.ToArray() is not [var next]
                        || next.Predecessors.Count != 1 || next.Index <= destination.Index || next.IsLandingPad
                        || next.Statements.Any(s => s is Phi))
                        break;
                    destination = next;
                }
                if (constructor is not { Op: null, Target: Const target, Arguments: [Use receiver, ..] }
                    || receiver.Variable != phi.Target) continue;
                var forwarded = new Dictionary<Phi, Block>();
                var paths = new List<Path>();
                if (!Collect(block, phi, [], paths, forwarded, [], out var aliases)
                    || paths.Select(a => a.Allocation.Op!.Operand).Distinct().ToArray() is not [DefinitionRef type]) continue;
                var aliasTargets = aliases.Select(p => p.Target).ToHashSet();
                var candidates = facts.EntriesAt(target.Value).Select(e => e.Method).OfType<MethodRef>()
                    .Where(m => m.Method.Name == ".ctor" && m.Method.Owner == type.Definition).Distinct().ToArray();
                if (candidates.Length == 0) continue;
                var signature = candidates is [var unique] ? abi.Machine(unique, out _) : null;
                var intermediates = paths.Select(p => p.Allocation.Defines[0]).Concat(forwarded.Keys.Select(p => p.Target)).ToArray();
                if (function.Blocks.SelectMany(b => b.Statements).Except(aliases).Except(forwarded.Keys)
                    .Any(s => intermediates.Any(v => AllocationPairing.Uses(s, v)))) continue;
                if (paths.Any(p => AllocationPairing.MotionProblem(function.Range, p.Source, p.Allocation, destination, constructor,
                    p.Middle.Concat(prefix), p.Allocation.Defines[0], signature) is not null)) continue;
                if (destination != block && function.Blocks.Any(current => current.Statements.Except(aliases).Any(s =>
                    s is Phi usePhi
                        ? usePhi.Sources.Where((v, edge) => aliasTargets.Contains(v)
                            && !dominators.Dominates(destination.Index, current.Predecessors[edge].Index)).Any()
                        : (!dominators.Dominates(destination.Index, current.Index) || prefix.Contains(s))
                            && aliasTargets.Any(v => AllocationPairing.Uses(s, v))))) continue;
                foreach (var alias in aliases) block.Statements.Remove(alias);
                foreach (var (forwarding, owner) in forwarded) owner.Statements.Remove(forwarding);
                foreach (var path in paths) path.Source.Statements.Remove(path.Allocation);
                var merged = paths[0].Allocation with { Address = constructor.Address, Defines = [phi.Target] };
                destination.Statements.Insert(destination.Statements.IndexOf(constructor), merged);
                foreach (var current in function.Blocks)
                    for (var i = 0; i < current.Statements.Count; i++)
                    {
                        if (current.Statements[i] is Phi p)
                            current.Statements[i] = p with { Sources = p.Sources.Select(v => aliasTargets.Contains(v) ? phi.Target : v).ToArray() };
                        else current.Statements[i] = ExprMap.MapOperands(current.Statements[i], e => ExprMap.Map(e,
                            x => x is Use u && aliasTargets.Contains(u.Variable) ? u with { Variable = phi.Target } : null));
                    }
                changed = true;
            }
        return changed;
    }

    private static bool SplitConstructorChoice(MachineFunction function, ProgramFacts facts, Func<int, Variable> fresh)
    {
        if (function.Range.ExceptionRegions.Length != 0) return false;
        foreach (var head in function.Blocks)
        {
            if (head.Handler is not null || head.Statements.LastOrDefault() is not Branch branch
                || head.NormalSuccessors.ToArray() is not [var yes, var no] || yes == no
                || yes.Start != branch.Target || no.Start != branch.Fallthrough || yes.Index <= head.Index || no.Index <= head.Index
                || head.Statements.OfType<Call>().LastOrDefault() is not { Op: { Kind: OpKind.NewObject, Operand: DefinitionRef type }, Defines: [var original] } allocation
                || head.Statements.Skip(head.Statements.IndexOf(allocation) + 1).Any(s =>
                    s is not (Nop or Branch or Define) || AllocationPairing.Uses(s, original)
                    || ExprMap.Operands(s).SelectMany(ExprMap.Walk).Any(e => e is Load or Intrinsic or Unknown))) continue;

            // 保留直接构造臂的原定义；共享构造臂只通过其入边 φ 使用新定义。
            var (direct, shared) = yes.Predecessors.Count == 1 ? (yes, no) : (no, yes);
            var side = shared.Predecessors.IndexOf(head);
            if (direct.Predecessors is not [var predecessor] || predecessor != head || shared.Predecessors.Count < 2
                || side < 0 || direct.Handler is not null || shared.Handler is not null || direct.IsLandingPad || shared.IsLandingPad
                || Constructor(direct, original, type) is null) continue;
            var aliases = shared.Statements.OfType<Phi>().Where(p => p.Sources.Length == shared.Predecessors.Count
                && p.Sources[side] == original).ToArray();
            if (aliases.Length == 0 || !aliases.Any(p => Constructor(shared, p.Target, type) is not null)) continue;
            var dominators = Dominators.Compute(function);
            if (function.Blocks.Any(b => b.Statements.Any(s => s is Phi p
                ? p.Sources.Where((v, i) => v == original && !(b == shared && i == side)
                    && !dominators.Dominates(direct.Index, b.Predecessors[i].Index)).Any()
                : !dominators.Dominates(direct.Index, b.Index) && AllocationPairing.Uses(s, original)))) continue;

            var label = Math.Max(NameSupply.JoinedEdgeBase, function.Blocks.Max(b => b.Start) + 1);
            var selected = fresh(original.Width);
            var edge = new Block(label) { End = label + 1, Statements =
                [allocation.Duplicate() with { Defines = [selected] }, new Jump(branch.Address, shared.Start)] };
            edge.Predecessors.Add(head);
            edge.Successors.Add(shared);
            head.Successors[head.Successors.IndexOf(shared)] = edge;
            shared.Predecessors[side] = edge;
            head.Statements.Remove(allocation);
            head.Statements[^1] = branch with
            {
                Target = branch.Target == shared.Start ? label : branch.Target,
                Fallthrough = branch.Fallthrough == shared.Start ? label : branch.Fallthrough,
            };
            foreach (var phi in aliases)
            {
                var sources = phi.Sources.ToArray();
                sources[side] = selected;
                shared.Statements[shared.Statements.IndexOf(phi)] = phi with { Sources = sources };
            }
            direct.Statements.Insert(0, allocation);
            function.Blocks.Add(edge);
            return true;
        }
        return false;

        Call? Constructor(Block block, Variable receiver, DefinitionRef type)
        {
            var statement = block.Statements.FirstOrDefault(s => s is not (Phi or Nop)
                && (s is not Define d || AllocationPairing.Uses(d, receiver)
                    || ExprMap.Walk(d.Value).Any(e => e is Load or Intrinsic or Unknown)));
            return statement is Call { Op: null, Target: Const target, Arguments: [Use actual, ..] } call
                && actual.Variable == receiver && facts.EntriesAt(target.Value).Any(e => e.Method?.Method is { Name: ".ctor" } ctor
                    && ctor.Owner == type.Definition) ? call : null;
        }
    }

    private static bool SelectConstantArguments(MachineFunction function)
    {
        foreach (var head in function.Blocks)
        {
            if (head.Handler is not null || head.Statements.LastOrDefault() is not Branch branch
                || head.Statements.OfType<Call>().LastOrDefault() is not { Op.Kind: OpKind.NewObject }
                || ExprMap.Walk(branch.Condition).Any(e => e is Load or Unknown or Intrinsic)
                || head.NormalSuccessors.ToArray() is not [var yes, var no] || yes == no
                || yes.Start != branch.Target || no.Start != branch.Fallthrough
                || !ConstantArm(yes, head) || !ConstantArm(no, head)
                || yes.NormalSuccessors.ToArray() is not [var join] || no.NormalSuccessors.ToArray() is not [var next]
                || join != next || join == head || join.IsLandingPad || join.Predecessors.Count != 2) continue;
            var phis = join.Statements.OfType<Phi>().ToArray();
            if (phis.Length == 0 || phis.Any(p => p.Sources.Length != 2)) continue;
            var yesIndex = join.Predecessors.IndexOf(yes);
            var noIndex = join.Predecessors.IndexOf(no);
            if (yesIndex < 0 || noIndex < 0) continue;
            var yesValues = yes.Statements.OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
            var noValues = no.Statements.OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
            if (phis.Any(p => !yesValues.ContainsKey(p.Sources[yesIndex]) || !noValues.ContainsKey(p.Sources[noIndex]))) continue;
            foreach (var phi in phis)
            {
                var taken = Fold.Convert(ConvKind.ZeroExtend, yesValues[phi.Sources[yesIndex]], phi.Target.Width);
                var skipped = Fold.Convert(ConvKind.ZeroExtend, noValues[phi.Sources[noIndex]], phi.Target.Width);
                join.Statements[join.Statements.IndexOf(phi)] = new Define(phi.Address, phi.Target,
                    new Select(branch.Condition, taken, skipped, phi.Target.Width));
            }
            head.Statements[^1] = new Jump(branch.Address, join.Start);
            MachineFunction.RemoveEdge(head, yes);
            MachineFunction.RemoveEdge(head, no);
            MachineFunction.AddEdge(head, join);
            return true;
        }
        return false;

        static bool ConstantArm(Block arm, Block head) => arm != head && arm.Handler is null && !arm.IsLandingPad
            && arm.Predecessors is [var predecessor] && predecessor == head
            && arm.Statements.All(s => s is Define { Value: Const } or Jump or Nop);
    }

    private static bool Collect(Block block, Phi phi, IReadOnlyList<Stmt> suffix, List<Path> paths,
        Dictionary<Phi, Block> forwarded, HashSet<Block> active, out Phi[] aliases)
    {
        aliases = [];
        if (phi.Sources.Length < 2 || phi.Sources.Length != block.Predecessors.Count || !active.Add(block)) return false;
        var sources = new HashSet<Variable>[phi.Sources.Length];
        var edge = 0;
        foreach (var (pred, source) in block.Predecessors.Zip(phi.Sources))
        {
            if (pred.NormalSuccessors.ToArray() is not [var next] || next != block) return false;
            var origin = pred;
            var tail = suffix;
            Path? path = null;
            while (true)
            {
                if (origin.Statements.OfType<Call>().FirstOrDefault(c => c.Op?.Kind == OpKind.NewObject
                    && c.Defines.FirstOrDefault() == source) is { } allocation)
                {
                    var remaining = origin.Statements.Skip(origin.Statements.IndexOf(allocation) + 1).Where(s => s is not Jump);
                    path = new(origin, allocation, [.. remaining, .. tail]);
                    break;
                }
                if (origin.IsLandingPad || origin.Statements.Any(s => s is Phi or Call)
                    || origin.Predecessors is not [var previous] || previous.Index >= origin.Index
                    || previous.NormalSuccessors.ToArray() is not [var successor] || successor != origin)
                    break;
                tail = [.. origin.Statements.Where(s => s is not Jump), .. tail];
                origin = previous;
            }
            if (path is not null)
            {
                paths.Add(path);
                sources[edge++] = [source];
                continue;
            }
            if (pred.IsLandingPad || pred.Handler is not null || pred.Statements.OfType<Phi>().FirstOrDefault(p => p.Target == source) is not { } incoming)
                return false;
            var middle = pred.Statements.Where(s => s is not (Phi or Jump)).Concat(suffix).ToArray();
            if (!Collect(pred, incoming, middle, paths, forwarded, active, out var incomingAliases)) return false;
            foreach (var alias in incomingAliases) forwarded.TryAdd(alias, pred);
            sources[edge++] = incomingAliases.Select(p => p.Target).ToHashSet();
        }
        active.Remove(block);
        aliases = block.Statements.OfType<Phi>().Where(p => p.Sources.Length == sources.Length
            && p.Sources.Zip(sources).All(pair => pair.Second.Contains(pair.First))).ToArray();
        return true;
    }
}
