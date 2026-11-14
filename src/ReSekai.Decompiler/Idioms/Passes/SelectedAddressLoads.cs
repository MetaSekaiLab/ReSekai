using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Ssa;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

/// <summary>条件地址改为单臂读取；已走过的分支可确认引用别名，字段身份仍由内存模型核对。</summary>
internal static class SelectedAddressLoads
{
    public static bool Run(MachineFunction function, Func<Variable> fresh)
    {
        var definitions = function.Blocks.SelectMany(b => b.Statements).OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
        var jumpInputs = new HashSet<Variable>();
        var pending = new Stack<Expr>(function.Blocks.SelectMany(b => b.Statements).OfType<IndirectJump>().Select(j => j.Target));
        while (pending.TryPop(out var expression))
            foreach (var use in ExprMap.Walk(expression).OfType<Use>())
                if (jumpInputs.Add(use.Variable) && definitions.TryGetValue(use.Variable, out var source)) pending.Push(source);
        Dominators? dominators = null;
        static bool Pure(Expr value) => !ExprMap.Walk(value).Any(e => e is Load or Intrinsic or Unknown
            or Binary { Op: BinOp.SDiv or BinOp.UDiv or BinOp.SRem or BinOp.URem });
        bool? KnownSide(Block block, Expr condition, bool throughMerges = false)
        {
            // 只沿唯一前驱追踪同一 SSA 条件（或它的否定）；可重读的内存和未转换的寄存器不能作为路径事实。
            if (!Pure(condition) || ExprMap.Walk(condition).Any(e => e is Read or Use { Variable.Version: < 0 })) return null;
            var negated = Fold.Not(condition);
            if (throughMerges)
            {
                dominators ??= Dominators.Compute(function);
                foreach (var candidate in function.Blocks)
                    if (candidate.Statements.LastOrDefault() is Branch decision && (decision.Condition == condition || decision.Condition == negated)
                        && decision.Target != decision.Fallthrough && candidate.NormalSuccessors.SingleOrDefault(b => b.Predecessors.Count == 1
                            && dominators.Dominates(b.Index, block.Index)) is { } taken)
                        return (taken.Start == decision.Target) == (decision.Condition == condition);
            }
            var seen = new HashSet<Block> { block };
            while (block.Predecessors.ToArray() is [var parent] && seen.Add(parent))
            {
                if (parent.Statements.LastOrDefault() is Branch branch && (branch.Condition == condition || branch.Condition == negated)
                    && branch.Target != branch.Fallthrough && parent.NormalSuccessors.Contains(block))
                    return (block.Start == branch.Target) == (branch.Condition == condition);
                block = parent;
            }
            return null;
        }
        Select? Choice(Expr address, HashSet<Variable> seen)
        {
            if (address is Use use && seen.Add(use.Variable) && definitions.TryGetValue(use.Variable, out var value))
            {
                var expression = Fold.Simplify(new Extract(value, 0, use.Width));
                return Choice(expression, seen);
            }
            if (address is Select select && Pure(select.Condition) && Pure(select.True) && Pure(select.False)) return select;
            if (address is Ir.Convert { Kind: ConvKind.ZeroExtend } extension && Choice(extension.Operand, seen) is { } extended)
                return new Select(extended.Condition, Fold.Simplify(extension with { Operand = extended.True }), Fold.Simplify(extension with { Operand = extended.False }), extension.Width);
            if (address is Extract { Low: 0 } extract && Choice(extract.Operand, seen) is { } extracted)
                return new Select(extracted.Condition, Fold.Simplify(extract with { Operand = extracted.True }), Fold.Simplify(extract with { Operand = extracted.False }), extract.Width);
            if (address is Binary { Op: BinOp.Shl, Right: Const } shift && Choice(shift.Left, seen) is { True: Const, False: Const } shifted)
                return new Select(shifted.Condition, Fold.Simplify(shift with { Left = shifted.True }), Fold.Simplify(shift with { Left = shifted.False }), shift.Width);
            if (address is not Binary { Op: BinOp.Add, Width: 64 } add) return null;
            if (Pure(add.Right) && Choice(add.Left, seen) is { } left)
                return left with { True = Fold.Simplify(add with { Left = left.True }), False = Fold.Simplify(add with { Left = left.False }) };
            if (Pure(add.Left) && Choice(add.Right, seen) is { } right)
                return right with { True = Fold.Simplify(add with { Right = right.True }), False = Fold.Simplify(add with { Right = right.False }) };
            return null;
        }
        var changed = false;
        foreach (var block in function.Blocks.ToArray())
            for (var i = 0; i < block.Statements.Count; i++)
            {
                var statement = block.Statements[i];
                if (statement is Store store && Pure(store.Value) && Choice(store.Target, []) is
                    { True: Binary { Op: BinOp.Add, Left: Use or Entry, Right: Const } yes,
                        False: Binary { Op: BinOp.Add, Left: Use or Entry, Right: Const } no } destination
                    && (yes.Left == no.Left || destination.Condition is Compare { Cond: Cond.Eq, Left: var tested, Right: Const { Value: 0 } }
                        && tested == no.Left || destination.Condition is Compare { Cond: Cond.Ne, Left: var nonNull, Right: Const { Value: 0 } }
                        && nonNull == yes.Left)
                    && SplitStore(function, block, i, store, destination)) return true;
                if (statement is Phi phi && phi.Sources.Length == block.Predecessors.Count)
                {
                    var sources = phi.Sources.ToArray();
                    for (var edge = 0; edge < sources.Length; edge++)
                    {
                        var predecessor = block.Predecessors[edge];
                        if (predecessor.NormalSuccessors.ToArray() is not [var next] || next != block
                            || predecessor.Handler is not null || !definitions.TryGetValue(sources[edge], out var source)
                            || source is not Select { Width: 64 } select || select.True is not FrameAddress && select.False is not FrameAddress
                            || KnownSide(predecessor, select.Condition, throughMerges: true) is not { } chosen
                            || (chosen ? select.True : select.False) is not FrameAddress frame) continue;
                        var replacement = fresh();
                        var position = predecessor.Statements.Count - (predecessor.Statements.LastOrDefault() is Jump ? 1 : 0);
                        predecessor.Statements.Insert(position, new Define(phi.Address, replacement, frame));
                        sources[edge] = replacement;
                        changed = true;
                    }
                    if (!sources.SequenceEqual(phi.Sources)) block.Statements[i] = phi with { Sources = sources };
                    continue;
                }
                var rewritten = ExprMap.MapOperands(statement, operand => ExprMap.Map(operand, expression =>
                {
                    if (expression is Use use && definitions.TryGetValue(use.Variable, out var definition)
                        && definition is Select { True: Use or Const or Symbol or FrameAddress, False: Use or Const or Symbol or FrameAddress } alias
                        && use.Width == alias.Width && KnownSide(block, alias.Condition) is { } aliasSide)
                        return aliasSide ? alias.True : alias.False;
                    if (expression is Select { True: Load, False: Load } selected
                        && KnownSide(block, selected.Condition) is { } selectedSide) return selectedSide ? selected.True : selected.False;
                    // 间接跳转从一次表项读取提取下标；保留选择地址，避免变成两个独立的读取。
                    if (statement is not Define define || jumpInputs.Contains(define.Target)
                        || expression is not Load load || Choice(load.Address, []) is not { } choice) return null;
                    return KnownSide(block, choice.Condition) is { } side ? load with { Address = side ? choice.True : choice.False }
                        : new Select(choice.Condition, load with { Address = choice.True }, load with { Address = choice.False }, load.Width);
                }));
                if (ReferenceEquals(rewritten, statement)) continue;
                block.Statements[i] = rewritten;
                changed = true;
            }
        return changed;
    }

    private static bool SplitStore(MachineFunction function, Block block, int index, Store store, Select destination)
    {
        if (block.Handler is not null || block.IsLandingPad || function.Range.ExceptionRegions.Length != 0)
            return false;
        var label = Math.Max(NameSupply.JoinedEdgeBase, function.Blocks.Max(b => b.Start) + 1);
        var merge = new Block(label + 2) { End = block.End, Statements = [.. block.Statements.Skip(index + 1)] };
        var yes = new Block(label) { End = label + 1, Statements = [store with { Target = destination.True }, new Jump(store.Address, merge.Start)] };
        var no = new Block(label + 1) { End = label + 2, Statements = [store with { Target = destination.False }, new Jump(store.Address, merge.Start)] };
        foreach (var successor in block.Successors)
        {
            successor.Predecessors[successor.Predecessors.IndexOf(block)] = merge;
            merge.Successors.Add(successor);
        }
        block.Successors.Clear();
        block.Successors.AddRange([yes, no]);
        yes.Predecessors.Add(block);
        no.Predecessors.Add(block);
        yes.Successors.Add(merge);
        no.Successors.Add(merge);
        merge.Predecessors.AddRange([yes, no]);
        block.Statements = [.. block.Statements.Take(index), new Branch(store.Address, destination.Condition, yes.Start, no.Start)];
        function.Blocks.AddRange([yes, no, merge]);
        function.Renumber();
        return true;
    }
}
