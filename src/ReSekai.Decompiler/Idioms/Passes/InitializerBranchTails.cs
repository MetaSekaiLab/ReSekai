using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;

namespace ReSekai.Decompiler.Idioms;

/// <summary>类初始化守卫的两条计算路径选择同一出口并传递相同的值时，使用已经初始化的路径。</summary>
internal static class InitializerBranchTails
{
    private abstract record Path;
    private sealed record Exit(Block From, Block Target) : Path;
    private sealed record Choice(Expr Condition, Path Yes, Path No) : Path;

    internal static bool Run(MachineFunction function, Func<Expr, Expr> expand)
    {
        foreach (var head in function.Blocks)
        {
            if (head.Handler is not null || head.Statements.LastOrDefault() is not Branch branch
                || head.Successors is not [var taken, var fallthrough] || taken == fallthrough
                || expand(branch.Condition) is not Compare { Cond: Cond.Eq or Cond.Ne, Left: Load flag, Right: Const { Value: 0 } } test
                || !EmptyArmBranches.IsBeforeFieldInitFlag(flag)) continue;
            var (fast, slow) = test.Cond == Cond.Ne ? (taken, fallthrough) : (fallthrough, taken);
            var definitions = new Dictionary<Variable, Expr>();
            var seen = new HashSet<Block>();
            var exits = new List<Exit>();
            bool guarded = false;
            Path? ReadPath(Block parent, Block block, int depth)
            {
                if (depth > 5 || block.Index <= parent.Index || block.Handler is not null) return null;
                if (block.Predecessors is not [var only] || only != parent
                    || block.Statements.Any(s => s is not (Define or Nop or Jump or Branch)))
                {
                    var exit = new Exit(parent, block);
                    exits.Add(exit);
                    return exit;
                }
                if (!seen.Add(block)) return null;
                foreach (var statement in block.Statements)
                {
                    if (statement is Nop { Text: ClassInitializationFacts.DeferredConstructor }) guarded = true;
                    if (statement is not Define define) continue;
                    if (!Pure(define.Value)) return null;
                    definitions.Add(define.Target, Expand(define.Value));
                }
                if (block.Statements.LastOrDefault() is Branch arm && Pure(arm.Condition) && block.Successors is [var yes, var no]
                    && ReadPath(block, yes, depth + 1) is { } a && ReadPath(block, no, depth + 1) is { } b)
                    return new Choice(Expand(arm.Condition), a, b);
                return block.Successors is [var next] ? ReadPath(block, next, depth + 1) : null;
            }
            Expr Expand(Expr value) => Fold.Simplify(ExprMap.Map(value, e => e is Use use && definitions.TryGetValue(use.Variable, out var definition)
                ? Fold.Convert(ConvKind.Truncate, definition, use.Width) : null));

            if (ReadPath(head, fast, 0) is not { } first || guarded || ReadPath(head, slow, 0) is not { } second || !guarded) continue;
            // 这些定义只能经出口 φ 流出；每个 φ 逐路径比较。
            if (function.Blocks.Where(b => !seen.Contains(b)).SelectMany(b => b.Statements)
                .Any(s => s is not Phi && ExprMap.Operands(s).SelectMany(ExprMap.Walk).OfType<Use>().Any(u => definitions.ContainsKey(u.Variable)))) continue;
            if (function.Blocks.Where(b => !seen.Contains(b)).Any(b => b.Statements.OfType<Phi>().Any(p =>
                p.Sources.Length != b.Predecessors.Count || p.Sources.Where((v, i) => definitions.ContainsKey(v)
                    && !exits.Contains(new Exit(b.Predecessors[i], b))).Any()))) continue;
            var atoms = new List<Expr>();
            Func<int, Exit> Compile(Path path)
            {
                if (path is Exit exit) return _ => exit;
                var choice = (Choice)path;
                var condition = CompileCondition(choice.Condition, atoms);
                var yes = Compile(choice.Yes);
                var no = Compile(choice.No);
                return bits => condition(bits) ? yes(bits) : no(bits);
            }
            var fastExit = Compile(first);
            var slowExit = Compile(second);
            if (atoms.Count > 6) continue;
            var widthsByUse = new Dictionary<Variable, int>();
            var forwarded = new Dictionary<Variable, List<Variable>>();
            foreach (var statement in function.Blocks.SelectMany(b => b.Statements))
            {
                if (statement is Phi phi)
                {
                    foreach (var source in phi.Sources)
                    {
                        if (!forwarded.TryGetValue(source, out var consumers)) forwarded[source] = consumers = [];
                        consumers.Add(phi.Target);
                    }
                }
                else foreach (var use in ExprMap.Operands(statement).SelectMany(ExprMap.Walk).OfType<Use>())
                    widthsByUse[use.Variable] = Math.Max(widthsByUse.GetValueOrDefault(use.Variable), use.Width);
            }
            int UsedWidth(Variable variable)
            {
                var reachable = new HashSet<Variable>();
                var queue = new Queue<Variable>();
                queue.Enqueue(variable);
                var width = 1;
                while (queue.TryDequeue(out var next))
                {
                    if (!reachable.Add(next)) continue;
                    width = Math.Max(width, widthsByUse.GetValueOrDefault(next));
                    foreach (var consumer in forwarded.GetValueOrDefault(next) ?? []) queue.Enqueue(consumer);
                }
                return width;
            }
            var widths = exits.SelectMany(e => e.Target.Statements.OfType<Phi>()).Distinct()
                .ToDictionary(p => p.Target, p => UsedWidth(p.Target));
            bool Same(int bits)
            {
                var a = fastExit(bits);
                var b = slowExit(bits);
                if (a.Target != b.Target) return false;
                var (i, j) = (a.Target.Predecessors.IndexOf(a.From), a.Target.Predecessors.IndexOf(b.From));
                return i >= 0 && j >= 0 && a.Target.Statements.OfType<Phi>().All(p =>
                {
                    var width = widths[p.Target];
                    var left = expand(Expand(new Use(p.Sources[i], width)));
                    var right = expand(Expand(new Use(p.Sources[j], width)));
                    return left == right;
                });
            }
            if (!Enumerable.Range(0, 1 << atoms.Count).All(Same)) continue;
            head.Statements[^1] = new Jump(branch.Address, fast.Start);
            MachineFunction.RemoveEdge(head, slow);
            return true;
        }
        return false;
    }

    private static bool Pure(Expr expression) => !ExprMap.Walk(expression).Any(e => e is Load or Read or Intrinsic or Unknown
        or Binary { Op: BinOp.SDiv or BinOp.UDiv or BinOp.SRem or BinOp.URem });

    private static Func<int, bool> CompileCondition(Expr condition, List<Expr> atoms)
    {
        if (condition is Const constant) return _ => constant.Value != 0;
        if (condition is Not not)
        {
            var operand = CompileCondition(not.Operand, atoms);
            return bits => !operand(bits);
        }
        if (condition is Logical logical)
        {
            var left = CompileCondition(logical.Left, atoms);
            var right = CompileCondition(logical.Right, atoms);
            return bits => logical.IsAnd ? left(bits) && right(bits) : left(bits) || right(bits);
        }
        if (condition is Compare { Cond: Cond.Eq or Cond.Ne, Right: Const { Value: 0 } } test && BooleanBits(test.Left, atoms) is { } normalized)
            return bits => normalized(bits) == (test.Cond == Cond.Ne);
        var index = atoms.IndexOf(condition);
        if (index >= 0) return bits => (bits & (1 << index)) != 0;
        var opposite = atoms.IndexOf(Fold.Not(condition));
        if (opposite >= 0) return bits => (bits & (1 << opposite)) == 0;
        var next = atoms.Count;
        atoms.Add(condition);
        return bits => (bits & (1 << next)) != 0;
    }

    private static Func<int, bool>? BooleanBits(Expr value, List<Expr> atoms)
    {
        switch (value)
        {
            case Ir.Convert { Kind: ConvKind.ZeroExtend or ConvKind.Truncate } conversion:
                return BooleanBits(conversion.Operand, atoms);
            case Extract { Low: 0 } slice:
                return BooleanBits(slice.Operand, atoms);
            case Select { True: Const { Value: 1 }, False: Const { Value: 0 } } yes:
                return CompileCondition(yes.Condition, atoms);
            case Select { True: Const { Value: 0 }, False: Const { Value: 1 } } no:
                return CompileCondition(Fold.Not(no.Condition), atoms);
            case Binary { Op: BinOp.And, Right: Const { Value: 1 } } mask:
                return BooleanBits(mask.Left, atoms);
            case Binary { Op: BinOp.And or BinOp.Or } binary when BooleanBits(binary.Left, atoms) is { } left
                && BooleanBits(binary.Right, atoms) is { } right:
                return bits => binary.Op == BinOp.And ? left(bits) && right(bits) : left(bits) || right(bits);
            default:
                return value.Width == 1 ? CompileCondition(value, atoms) : null;
        }
    }
}
