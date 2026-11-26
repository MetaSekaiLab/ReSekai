using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Ssa;

namespace ReSekai.Decompiler.Idioms;

/// <summary>
/// 循环条件推进栈上的状态（内联的枚举器 MoveNext：下标加一、写回、与长度比较），循环旋转把它复制到入口和回边各一份。
/// 两份逐项对应时合回循环头上的一份，让推进与比较重新成为每轮开头的同一次调用。
/// </summary>
internal static class RotatedFrameGuards
{
    public static bool Run(MachineFunction function, Func<int, Variable> fresh)
    {
        Dominators? dominators = null;
        foreach (var head in function.Blocks)
        {
            if (head.IsLandingPad || head.Predecessors.Count != 2 || head.Predecessors[0] == head.Predecessors[1]) continue;
            for (var side = 0; side < 2; side++)
            {
                var (entry, latch) = (head.Predecessors[side], head.Predecessors[1 - side]);
                if (entry.Handler is not null || latch.Handler is not null || entry.Statements.LastOrDefault() is not Branch first || latch.Statements.LastOrDefault() is not Branch second
                    || Exit(entry, head) is not { IsLandingPad: false } exit || Exit(latch, head) != exit) continue;
                dominators ??= Dominators.Compute(function);
                if (!dominators.Dominates(head.Index, latch.Index) || dominators.Dominates(head.Index, entry.Index)) continue;
                if (new Merge(function, head, entry, latch, exit, fresh).Run(Taken(first, head), Taken(second, head))) return true;
            }
        }
        return false;
    }

    private static Block? Exit(Block block, Block head) => block.NormalSuccessors.ToArray() is [var a, var b] && a != b
        ? a == head ? b : b == head ? a : null : null;

    /// <summary>进入循环体的条件。</summary>
    private static Expr? Taken(Branch branch, Block head) => branch.Target == head.Start ? branch.Condition
        : branch.Condition is Compare { Left.Width: <= 64 } compare && Fold.Invert(compare.Cond) is var inverted ? compare with { Cond = inverted } : null;

    private sealed class Merge(MachineFunction function, Block head, Block entry, Block latch, Block exit, Func<int, Variable> fresh)
    {
        private readonly Dictionary<(Variable, Variable), Variable> merged = [];
        private readonly HashSet<(Variable, Variable)> recomputed = [];
        private readonly List<Phi> phis = [];
        private readonly List<Stmt> body = [];

        public bool Run(Expr? first, Expr? second)
        {
            if (first is not Compare || second is not Compare || Unify(first, second, define: true) is not { } condition) return false;
            // 推进后的值写回栈上的状态：两份都写同一个位置，其后没有重叠的访问。
            var entryTail = Tail(entry);
            var latchTail = Tail(latch);
            var moved = new List<(Store Entry, Store Latch)>();
            foreach (var store in entryTail.OfType<Store>())
                if (store is { Target: FrameAddress slot, Value: Use value }
                    && latchTail.OfType<Store>().LastOrDefault(s => s.Target == slot && s.Value is Use other && other.Width == value.Width
                        && recomputed.Contains((value.Variable, other.Variable))) is { } counterpart
                    && Movable(entryTail, store, moved.Select(m => m.Entry)) && Movable(latchTail, counterpart, moved.Select(m => m.Latch)))
                    moved.Add((store, counterpart));
            if (moved.Count == 0) return false;
            foreach (var (store, counterpart) in moved)
            {
                var value = (Use)store.Value;
                body.Add(store with { Value = new Use(merged[(value.Variable, ((Use)counterpart.Value).Variable)], value.Width) });
            }
            var label = Math.Max(NameSupply.JoinedEdgeBase, function.Blocks.Max(b => b.Start) + 1);
            var address = latch.Statements[^1].Address;
            var guard = new Block(label) { End = label + 1 };
            foreach (var (block, side) in new[] { (entry, 0), (latch, 1) })
            {
                foreach (var pair in moved) block.Statements.Remove(side == 0 ? pair.Entry : pair.Latch);
                block.Statements[^1] = new Jump(block.Statements[^1].Address, label);
            }
            // 循环头与出口的合流改由新块提供。
            var (headEntry, headLatch) = (head.Predecessors.IndexOf(entry), head.Predecessors.IndexOf(latch));
            for (var i = 0; i < head.Statements.Count && head.Statements[i] is Phi phi; i++)
                head.Statements[i] = new Define(phi.Address, phi.Target, new Use(Joined(phi.Sources[headEntry], phi.Sources[headLatch]), phi.Target.Width));
            var (exitEntry, exitLatch) = (exit.Predecessors.IndexOf(entry), exit.Predecessors.IndexOf(latch));
            var kept = Enumerable.Range(0, exit.Predecessors.Count).Where(i => i != exitEntry && i != exitLatch).ToArray();
            for (var i = 0; i < exit.Statements.Count && exit.Statements[i] is Phi phi; i++)
                exit.Statements[i] = phi with { Sources = [.. kept.Select(k => phi.Sources[k]), Joined(phi.Sources[exitEntry], phi.Sources[exitLatch])] };
            var others = kept.Select(k => exit.Predecessors[k]).ToArray();
            exit.Predecessors.Clear();
            exit.Predecessors.AddRange([.. others, guard]);
            head.Predecessors.Clear();
            head.Predecessors.Add(guard);
            foreach (var block in new[] { entry, latch })
            {
                block.Successors.Clear();
                MachineFunction.AddEdge(block, guard);
            }
            guard.Successors.AddRange([head, exit]);
            guard.Statements = [.. phis, .. body, new Branch(address, condition, head.Start, exit.Start)];
            function.Blocks.Add(guard);
            function.Renumber();
            // 单一前驱的出口不需要合流。
            if (exit.Predecessors.Count == 1)
                for (var i = 0; i < exit.Statements.Count && exit.Statements[i] is Phi phi; i++)
                    exit.Statements[i] = new Define(phi.Address, phi.Target, new Use(phi.Sources[0], phi.Target.Width));
            return true;
        }

        /// <summary>块末尾只有纯定义与栈写入的一段。</summary>
        private static List<Stmt> Tail(Block block)
        {
            var tail = block.Statements.Reverse().TakeWhile(s => s is Define or Nop or Jump or Branch || s is Store { Target: FrameAddress }).ToList();
            tail.Reverse();
            return tail;
        }

        /// <summary>写入移到末尾：其后没有重叠的写入（同样移走的除外）和读取。</summary>
        private static bool Movable(List<Stmt> tail, Store store, IEnumerable<Store> movedStores)
        {
            var slot = ((FrameAddress)store.Target).Offset;
            bool Overlaps(Expr target, int width) => target is not FrameAddress other || other.Offset < slot + store.Value.Width / 8 && slot < other.Offset + width / 8;
            return tail.Skip(tail.IndexOf(store) + 1).All(s => s switch
            {
                Store later => movedStores.Contains(later) || !Overlaps(later.Target, later.Value.Width),
                _ => ExprMap.Operands(s).SelectMany(ExprMap.Walk).OfType<Load>().All(l => l.Address is FrameAddress && !Overlaps(l.Address, l.Width)),
            });
        }

        private Variable Joined(Variable first, Variable second)
        {
            if (first == second) return first;
            if (merged.TryGetValue((first, second), out var known)) return known;
            var target = fresh(first.Width);
            phis.Add(new Phi(latch.Statements[^1].Address, target, [first, second]));
            return merged[(first, second)] = target;
        }

        /// <summary>两份表达式逐项对应：变量在各自块内由对应的算式算出时合成一次计算，否则合流。</summary>
        private Expr? Unify(Expr first, Expr second, bool define)
        {
            if (first == second && first is Const or FrameAddress or Symbol) return first;
            switch (first, second)
            {
                case (Use a, Use b) when a.Width == b.Width && a.Variable.Width == b.Variable.Width:
                    if (a.Variable == b.Variable) return a;
                    if (merged.TryGetValue((a.Variable, b.Variable), out var known)) return new Use(known, a.Width);
                    if (define && Definition(entry, a.Variable) is { } left && Definition(latch, b.Variable) is { } right
                        && left.Value is Binary or Ir.Convert && Unify(left.Value, right.Value, define) is { } value)
                    {
                        var target = fresh(a.Variable.Width);
                        body.Add(new Define(right.Address, target, value));
                        recomputed.Add((a.Variable, b.Variable));
                        return new Use(merged[(a.Variable, b.Variable)] = target, a.Width);
                    }
                    return new Use(Joined(a.Variable, b.Variable), a.Width);
                case (Binary a, Binary b) when a.Op == b.Op && a.Width == b.Width:
                    return Unify(a.Left, b.Left, define) is { } binaryLeft && Unify(a.Right, b.Right, define) is { } binaryRight ? a with { Left = binaryLeft, Right = binaryRight } : null;
                case (Compare a, Compare b) when a.Cond == b.Cond:
                    return Unify(a.Left, b.Left, define) is { } compareLeft && Unify(a.Right, b.Right, define) is { } compareRight ? a with { Left = compareLeft, Right = compareRight } : null;
                case (Ir.Convert a, Ir.Convert b) when a.Kind == b.Kind && a.Width == b.Width:
                    return Unify(a.Operand, b.Operand, define) is { } operand ? a with { Operand = operand } : null;
                case (Extract a, Extract b) when a.Low == b.Low && a.Width == b.Width:
                    return Unify(a.Operand, b.Operand, define) is { } source ? a with { Operand = source } : null;
                default:
                    return first == second && !ExprMap.Walk(first).Any(e => e is Load or Use) ? first : null;
            }
        }

        private static Define? Definition(Block block, Variable variable) => block.Statements.OfType<Define>().LastOrDefault(d => d.Target == variable);
    }
}
