using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Storage;
using ReSekai.Decompiler.Recovery.Runtime;

namespace ReSekai.Decompiler.Idioms;

/// <summary>
/// 幂等初始化守卫：if (!flag) init(); 之后 clang 可把后续计算复制到两臂，并让快路径复用守卫之前读到的值。
/// 快路径上初始化已经完成，在其开头补上 init 也不改变行为，所以两臂的差别只在写法：
/// 把两臂的写入、合流值或出口分支归一到“守卫处的内存内容”后逐项比较，相同时只保留初始化一臂。
/// 类构造、类布局与方法元数据的守卫共用这一证明；证明不成立时保留原分支并记录原因。
/// </summary>
internal static class OnceGuardsPass
{
    private enum Kind { Metadata, ClassConstructor, ClassLayout }
    private sealed record Guard(Kind Kind, Block Initialize, Block Skip, TypeRef? Type, ulong Flag, HashSet<Variable> FlagPhis);

    /// <summary>折叠一个守卫；调用方在清理后继续寻找下一个。</summary>
    internal static bool OnceGuards(this IdiomState state)
    {
        foreach (var head in state.function.Blocks)
        {
            if (state.removed.Contains(head) || head.Statements.LastOrDefault() is not Branch branch || Recognize(state, head, branch) is not { } guard) continue;
            var proof = new Proof(state, head, guard);
            if (!proof.Holds(out var reason))
            {
                state.Reject($"once.{guard.Kind}", branch.Address, reason);
                continue;
            }
            if (guard.Kind == Kind.Metadata) proof.CommitMetadata(branch);
            head.Statements[^1] = new Jump(branch.Address, guard.Initialize.Start);
            MachineFunction.RemoveEdge(head, guard.Skip);
            return true;
        }
        return false;
    }

    private static Guard? Recognize(IdiomState state, Block head, Branch branch)
    {
        if (head.NormalSuccessors.ToArray() is not [var first, var second]) return null;
        var condition = FlagPhis(state, state.defs.Expand(branch.Condition), out var flags);
        if (TopLoadsPass.TopLoads(condition).Distinct().ToArray() is not [var flag]) return null;
        var side = DirectionPass.Direction(condition, ReplacePass.Replace(flag, 0));
        if (side is not (0 or 1) || DirectionPass.Direction(condition, ReplacePass.Replace(flag, 1)) != 1 - side) return null;
        var (initialize, skip) = side == 0 ? (first, second) : (second, first);
        switch (flag)
        {
            case { Width: 8, Address: Const address } when state.owner.guardSections.Any(s => s.Contains(address.Value)):
                return new(Kind.Metadata, initialize, skip, null, address.Value, flags);
            case { Width: 32, Address: Binary { Op: BinOp.Add, Left: Symbol { Kind: SymbolKind.Class, Value: TypeRef type }, Right: Const { Value: RuntimeLayout.ClassCctorFinishedOrNoCctor } } }:
                // 类自身的静态构造中，标志位的含义不同。
                return state.self?.Method.Name == ".cctor" && type is DefinitionRef definition
                    && state.self.Method.Owner == definition.Definition ? null : new(Kind.ClassConstructor, initialize, skip, type, 0, []);
            case { Width: 8 or 16, Address: Binary { Op: BinOp.Add, Left: Symbol { Kind: SymbolKind.Class, Value: TypeRef type }, Right: Const { Value: RuntimeLayout.ClassBitflags1 } } }
                when ClassInitializationFlags.IsBitTest(condition, flag):
                return new(Kind.ClassLayout, initialize, skip, type, 0, []);
            default:
                return null;
        }
    }

    /// <summary>
    /// 循环里的守卫只读一次标志，此后的迭代用常量 1：标志读取与 1 的汇合仍是“是否已初始化”。
    /// 常量一侧初始化已经完成，再做一次幂等的初始化不改变行为，按标志的读取识别。
    /// </summary>
    private static Expr FlagPhis(IdiomState state, Expr condition, out HashSet<Variable> seen)
    {
        seen = [];
        if (TopLoadsPass.TopLoads(condition).Any()) return condition;
        var phis = ExprMap.Walk(condition).OfType<Use>().Select(u => u.Variable).Distinct().ToArray();
        if (phis is not [var variable]) return condition;
        var pending = new Stack<Variable>([variable]);
        Load? flag = null;
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (state.function.Blocks.SelectMany(b => b.Statements).OfType<Phi>().FirstOrDefault(p => p.Target == current) is { } phi)
            {
                foreach (var source in phi.Sources) pending.Push(source);
                continue;
            }
            var value = state.defs.Expand(new Use(current, current.Width));
            while (value is Ir.Convert { Kind: ConvKind.ZeroExtend } extended) value = extended.Operand;
            if (value is Const { Value: 1 }) continue;
            if (value is not Load { Width: 8, Address: Const } load || flag is not null && flag != load) return condition;
            flag = load;
        }
        if (flag is not { Address: Const at } || !state.owner.guardSections.Any(s => s.Contains(at.Value))) return condition;
        return Fold.Simplify(ExprMap.Map(condition, e => e is Use use && use.Variable == variable
            ? use.Width == 8 ? flag : Fold.Convert(use.Width < 8 ? ConvKind.Truncate : ConvKind.ZeroExtend, flag, use.Width) : null));
    }

    /// <summary>一臂的走法：环境、写入与终点。</summary>
    private sealed class Line
    {
        public Dictionary<Variable, Expr> Values { get; } = [];
        /// <summary>守卫之前的定义归一后的结果。</summary>
        public Dictionary<Variable, Expr> Expanded { get; } = [];
        public List<(Expr Target, Expr Value)> Stores { get; } = [];
        /// <summary>初始化调用前后的寄存器暂存：帧槽偏移、位数与值。</summary>
        public List<(long Offset, int Width, Expr Value)> Spills { get; } = [];
        public Block Previous { get; set; } = null!;
        public Block? Merge { get; set; }
        public Branch? Exit { get; set; }
        public Return? Returned { get; set; }
        public bool Initialized { get; set; }
        public List<ulong> Slots { get; } = [];
        public Store? FlagStore { get; set; }
        public Block? FlagBlock { get; set; }
    }

    private sealed class Proof(IdiomState state, Block head, Guard guard)
    {
        private const int Depth = 32;
        private readonly Dictionary<Variable, Expr> definitions = state.function.Blocks.SelectMany(b => b.Statements).OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
        private readonly Dictionary<Variable, Block> owners = state.function.Blocks.SelectMany(b => b.Statements.OfType<Define>().Select(d => (d.Target, Block: b))).ToDictionary(p => p.Target, p => p.Block);
        private readonly Dictionary<Variable, Variable> copies = state.function.Blocks.SelectMany(b => b.Statements).OfType<Phi>()
            .Where(p => p.Sources.Length > 0 && p.Sources.All(v => v == p.Sources[0]) && p.Sources[0].Version >= 0)
            .ToDictionary(p => p.Target, p => p.Sources[0]);
        private readonly Dictionary<Variable, bool> unchanged = [];
        private Func<Expr, TypeRef?>? types;
        private FrameModel? frame;
        private bool loadAfterStore;
        private Line? slow;

        public bool Holds(out string reason)
        {
            var fast = new Line();
            if (!Walk(fast, guard.Skip, false, null, out reason)) return false;
            var taken = new Line();
            slow = taken;
            var merge = fast.Merge;
            if (merge is null && fast.Exit is null && fast.Returned is null) { reason = "快路径没有终点"; return false; }
            if (!Walk(taken, guard.Initialize, true, merge, out reason)) return false;
            if (merge is not null && taken.Exit is not null && guard.Kind == Kind.Metadata
                && merge.Statements.LastOrDefault() is Branch && merge.Statements.All(s => s is Phi or Nop or Branch))
            {
                // 快路径复用前面读过的值，在共享分支块取本入边的 φ 来源后再比较出口。
                fast.Exit = (Branch)merge.Statements[^1];
                var incoming = merge.Predecessors.IndexOf(fast.Previous);
                if (incoming < 0 || merge.Statements.OfType<Phi>().Any(p => p.Sources.Length != merge.Predecessors.Count))
                { reason = "共享分支的入边不明"; return false; }
                foreach (var phi in merge.Statements.OfType<Phi>())
                    fast.Values[phi.Target] = Normal(new Use(phi.Sources[incoming], phi.Target.Width), fast, 0);
                fast.Previous = merge;
                merge = null;
            }
            // 同一标志的前一个守卫已经初始化时，这一臂的调用被清理掉，只剩写标志。
            if (!taken.Initialized && taken.FlagStore is null) { reason = "慢路径没有初始化"; return false; }
            if (loadAfterStore) { reason = "写入之后还有读取"; return false; }
            // 两臂各写一次同一个局部、内容相同：是守卫之后的写入被复制到两臂，不是暂存。
            foreach (var spill in taken.Spills.ToArray())
                if (fast.Stores.Where(s => s.Target is FrameAddress at && at.Offset < spill.Offset + spill.Width / 8 && spill.Offset < at.Offset + s.Value.Width / 8).ToArray()
                        is [{ Target: FrameAddress same } store] && same.Offset == spill.Offset && store.Value == spill.Value
                    && taken.Spills.Count(s => s.Offset < spill.Offset + spill.Width / 8 && spill.Offset < s.Offset + s.Width / 8) == 1)
                {
                    fast.Stores.Remove(store);
                    taken.Spills.Remove(spill);
                }
            if (Remaining(taken, merge) is { } observed) { reason = $"慢路径的暂存之后仍被读取 {observed}"; return false; }
            if (fast.Stores.Count != taken.Stores.Count || !fast.Stores.SequenceEqual(taken.Stores)) { reason = "两臂的写入不同"; return false; }
            if (merge is not null)
            {
                if (taken.Merge != merge) { reason = "两臂的汇合点不同"; return false; }
                var (a, b) = (merge.Predecessors.IndexOf(fast.Previous), merge.Predecessors.IndexOf(taken.Previous));
                if (a < 0 || b < 0) { reason = "两臂的汇合边不明"; return false; }
                foreach (var phi in merge.Statements.OfType<Phi>())
                {
                    // 标志只会是 0 或 1，跳过初始化的一臂上它就是 1。
                    if (guard.FlagPhis.Contains(phi.Target)) continue;
                    var width = Observed(phi.Target, []);
                    if (width == 0 || phi.Sources.Length != merge.Predecessors.Count) continue;
                    var (skipped, initialized) = (Normal(new Use(phi.Sources[a], width), fast, 0), Normal(new Use(phi.Sources[b], width), taken, 0));
                    // 生成代码在守卫里只做初始化，不给局部赋值：这种 φ 只能来自 clang 的部分冗余消除，
                    // 快路径复用已有的值（不是读取），初始化可能改写内存的一臂重新读取。保留的正是重新读取的一臂。
                    // 两臂都是读取而位置不同时不成立。初始化调用破坏的寄存器不会再被真正读取，这一臂的值没有意义。
                    static bool Reads(Expr value) => ExprMap.Walk(value).Any(e => e is Load);
                    if (Plain(skipped) != Plain(initialized) && (Reads(skipped) || !Reads(initialized)) && initialized is not Use { Variable.Version: -1 }
                        && !SameInLoop(phi.Sources[a], phi.Sources[b], width))
                    { reason = "两臂的合流值不同"; return false; }
                }
                reason = "";
                return true;
            }
            if (fast.Returned is { } finished)
            {
                if (taken.Returned is not { } returned) { reason = "两臂的终点不同"; return false; }
                var (expected, actual) = (ExprMap.Operands(finished).ToArray(), ExprMap.Operands(returned).ToArray());
                if (expected.Length != actual.Length || !expected.Zip(actual).All(p => Plain(Normal(p.First, fast, 0)) == Plain(Normal(p.Second, taken, 0))))
                { reason = "两臂的返回值不同"; return false; }
                reason = "";
                return true;
            }
            if (taken.Exit is not { } after || fast.Exit is not { } normal) { reason = "两臂的终点不同"; return false; }
            var left = Normal(after.Condition, taken, 0);
            var right = Normal(normal.Condition, fast, 0);
            bool Same(Variable x, Variable y) => Normal(new Use(x, x.Width), taken, 0) == Normal(new Use(y, y.Width), fast, 0);
            bool Exit(ulong x, ulong y) => ClassGuardExits.Same(taken.Previous, x, fast.Previous, y, Same);
            if (left == right && Exit(after.Target, normal.Target) && Exit(after.Fallthrough, normal.Fallthrough)
                || left == Fold.Simplify(new Not(right)) && Exit(after.Target, normal.Fallthrough) && Exit(after.Fallthrough, normal.Target))
            {
                reason = "";
                return true;
            }
            reason = "两臂的出口分支不同";
            return false;
        }

        /// <summary>离开慢路径之后还能被观察、且内容与守卫之前不同的暂存。</summary>
        private string? Remaining(Line line, Block? merge)
        {
            if (line.Spills.Count == 0 || line.Returned is not null) return null;
            var ends = merge is not null ? [merge] : line.Previous.NormalSuccessors.ToArray();
            foreach (var (offset, width, value) in line.Spills)
            {
                if (ends.All(b => frame!.Dead(b, 0, offset, width / 8))) continue;
                var last = line.Spills.Last(s => s.Offset < offset + width / 8 && offset < s.Offset + s.Width / 8);
                if (last == (offset, width, value) && Content(new Load(new FrameAddress(offset), width), new Line(), 0) == value) continue;
                return new FrameAddress(offset).ToString();
            }
            return null;
        }

        /// <summary>
        /// 循环里不变的值：调用破坏寄存器之后重新取出（常量重新物化、从帧槽重载），与没有调用的一路在循环头合流。
        /// 一组 φ 的所有外部来源都是同一个表达式，表达式的变量又都在这组 φ 之前定义好时，整组就是这个值。
        /// </summary>
        private bool SameInLoop(Variable left, Variable right, int width)
        {
            var phis = state.function.Blocks.SelectMany(b => b.Statements.OfType<Phi>().Select(p => (Phi: p, Block: b))).ToDictionary(p => p.Phi.Target);
            var web = new HashSet<Variable>();
            var pending = new Stack<Variable>([left, right]);
            Expr? agreed = null;
            while (pending.TryPop(out var variable))
            {
                if (phis.TryGetValue(variable, out var phi))
                {
                    if (!web.Add(variable) || web.Count > 64) { if (web.Count > 64) return false; continue; }
                    foreach (var source in phi.Phi.Sources) pending.Push(source);
                    continue;
                }
                if (Stable(new Use(variable, width), 0) is not { } value) return false;
                value = Plain(value);
                if (agreed is not null && agreed != value) return false;
                agreed = value;
            }
            if (agreed is null) return false;
            var used = ExprMap.Walk(agreed).OfType<Use>().Select(u => u.Variable).Distinct().ToArray();
            if (used.Length == 0) return true;
            var blocks = web.Select(w => phis[w].Block).ToHashSet();
            return used.All(u => u.Version == 0 || Defined(u) is { } at && Precedes(at, blocks));
        }

        /// <summary>从入口到这些块的每条路径都先经过 block。</summary>
        private bool Precedes(Block block, HashSet<Block> blocks)
        {
            var visited = new HashSet<Block> { block };
            var pending = new Stack<Block>([state.function.EntryBlock]);
            if (blocks.Contains(block)) return false;
            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current)) continue;
                if (blocks.Contains(current)) return false;
                foreach (var next in current.Successors) pending.Push(next);
            }
            return true;
        }

        private Block? Defined(Variable variable) => state.function.Blocks.FirstOrDefault(b => b.Statements.Any(s => s switch
        {
            Define d => d.Target == variable,
            Phi p => p.Target == variable,
            Call c => c.Defines.Contains(variable),
            _ => false,
        }));

        /// <summary>只由 SSA 值与常量组成的写法；帧槽的读取换成到达它的唯一写入，其他读取随程序点变化，不算。</summary>
        private Expr? Stable(Expr expression, int depth)
        {
            if (depth > 8) return null;
            var failed = false;
            var result = Fold.Simplify(ExprMap.Map(expression, part =>
            {
                switch (part)
                {
                    case Use use when definitions.TryGetValue(use.Variable, out var source):
                        if (source is Ir.Convert { Kind: ConvKind.ZeroExtend, Operand: Load } or Load)
                            source = Reloaded(use.Variable, source);
                        // 取不出稳定写法的定义仍是一个确定的 SSA 值。
                        if (source is null || Stable(source, depth + 1) is not { } value) return use;
                        return value.Width == use.Width ? value : Fold.Convert(ConvKind.Truncate, value, use.Width);
                    case Load or Unknown or Intrinsic:
                        failed = true;
                        return part;
                    default:
                        return null;
                }
            }));
            return failed ? null : result;
        }

        /// <summary>从读取处往回走到写入之前，写入的变量是否又被定义：那样槽里是上一轮的值。</summary>
        private static bool Redefined(Block block, int index, IReadOnlyList<Store> stores, HashSet<Variable> variables)
        {
            var visited = new HashSet<Block>();
            var pending = new Stack<(Block Block, int End)>([(block, index)]);
            while (pending.TryPop(out var current))
            {
                var stopped = false;
                for (var i = current.End - 1; i >= 0 && !stopped; i--)
                {
                    var statement = current.Block.Statements[i];
                    stopped = statement is Store store && stores.Contains(store);
                    if (!stopped && statement switch
                    {
                        Define d => variables.Contains(d.Target),
                        Phi p => variables.Contains(p.Target),
                        Call c => c.Defines.Any(variables.Contains),
                        _ => false,
                    }) return true;
                }
                if (stopped) continue;
                foreach (var predecessor in current.Block.Predecessors)
                    if (visited.Add(predecessor)) pending.Push((predecessor, predecessor.Statements.Count));
            }
            return false;
        }

        private Expr? Reloaded(Variable variable, Expr source)
        {
            var load = source as Load ?? (Load)((Ir.Convert)source).Operand;
            if (load.Address is not FrameAddress { Offset: < 0 and var offset } || !owners.TryGetValue(variable, out var block)) return null;
            var index = block.Statements.FindIndex(s => s is Define define && define.Target == variable);
            var reach = (frame ??= new FrameModel(state.function, state.owner.CallEffects)).Reach(block, index, offset, load.Width / 8);
            if (!reach.Complete || reach.Stores.Select(s => (s.Target, s.Value)).Distinct().ToArray() is not [(FrameAddress target, var stored)]) return null;
            if (target.Offset > offset || target.Offset + stored.Width / 8 < offset + load.Width / 8) return null;
            if (Redefined(block, index, reach.Stores, ExprMap.Walk(stored).OfType<Use>().Select(u => u.Variable).ToHashSet())) return null;
            var value = Fold.Extract(stored, checked((int)(offset - target.Offset) * 8), load.Width);
            return value.Width == source.Width ? value : Fold.Convert(ConvKind.ZeroExtend, value, source.Width);
        }

        public void CommitMetadata(Branch branch)
        {
            var line = slow!;
            if (line.FlagStore is { } set && line.FlagBlock is { } block)
            {
                block.Statements[block.Statements.IndexOf(set)] = new Nop(set.Address, "MetadataInitGuard");
                state.initializedGuards.Add((block, guard.Flag));
            }
            state.Result.GuardSlots[guard.Flag] = [.. state.Result.GuardSlots.GetValueOrDefault(guard.Flag, []).Union(line.Slots).Order()];
            state.Result.InitializedSlots.UnionWith(line.Slots);
            state.Result.MetadataGuardBranches.Add(branch.Address);
            state.Result.Hit(Template.MetadataInitGuard);
        }

        private bool Walk(Line line, Block start, bool initializing, Block? stop, out string reason)
        {
            reason = "";
            var visited = new HashSet<Block>();
            var (previous, current) = (head, start);
            while (true)
            {
                // 常量返回的跳板可以与其他路径共用，真正的合流发生在返回值合并处。
                var shared = current.Predecessors.Count > 1;
                var forwarding = guard.Kind == Kind.Metadata && shared && current.Statements.All(s => s is Nop or Jump or Define { Value: Const })
                    && current.Handler is null && !current.IsLandingPad && current.NormalSuccessors.Count() == 1;
                if (current == stop || !initializing && shared && !forwarding)
                {
                    line.Previous = previous;
                    line.Merge = current;
                    return true;
                }
                // 没有异常边只说明块里没有原生抛出点，不说明它在 try 外：只有两处都有异常边且去向不同才算跨区域。
                if (!visited.Add(current) || current.IsLandingPad || current.Handler is not null && head.Handler is not null && current.Handler != head.Handler)
                { reason = "路径含异常边或环"; return false; }
                line.Previous = previous;
                var incoming = current.Predecessors.IndexOf(previous);
                Block? onward = null;
                foreach (var statement in current.Statements)
                {
                    switch (statement)
                    {
                        case Nop or Jump:
                            break;
                        case Phi phi:
                            if (incoming < 0 || phi.Sources.Length != current.Predecessors.Count) { reason = "路径上的 φ 不明"; return false; }
                            line.Values[phi.Target] = Normal(new Use(phi.Sources[incoming], phi.Target.Width), line, 0);
                            break;
                        // 快路径直接到达汇合点，初始化一臂重新读取之后多一次抛出判断（判空、越界）：
                        // 快路径的值已经判过，保留这一臂时判断照常执行，结果相同。
                        case Branch when initializing && stop is not null && !shared && Survivor(current) is { } continuing:
                            onward = continuing;
                            break;
                        case Branch test when initializing && guard.Kind == Kind.Metadata && stop is not null && !shared
                            && line.Stores.Count == 0 && ReloadedNonNull(test, current, line) is { } nonNull:
                            onward = nonNull;
                            break;
                        case Branch exit when !shared:
                            line.Previous = current;
                            line.Exit = exit;
                            return true;
                        case Return finished when !shared:
                            line.Previous = current;
                            line.Returned = finished;
                            return true;
                        case Define define when forwarding || !shared && !ExprMap.Walk(define.Value).Any(e => e is Intrinsic or Unknown):
                            line.Values[define.Target] = Normal(define.Value, line, 0);
                            break;
                        case Store { Target: Const target, Value: Const { Value: 1, Width: 8 } } set when initializing && guard.Kind == Kind.Metadata && target.Value == guard.Flag:
                            line.FlagStore = set;
                            line.FlagBlock = current;
                            break;
                        // 守卫内的暂存若没有后续观察者，不参与两臂的内存效果比较。
                        case Store { Target: FrameAddress, Value: Use or Const } spill when !shared && guard.Kind == Kind.Metadata
                            && (frame ??= new FrameModel(state.function, state.owner.CallEffects)).UnobservedAfter(current, spill):
                            break;
                        // 初始化调用前把寄存器存到帧槽、之后取回：只在这一臂里可见，终点处再核对没有别的观察者。
                        case Store { Target: FrameAddress { Offset: < 0 } slot } spill when initializing && !shared && line.Stores.Count == 0
                            && !ExprMap.Walk(spill.Value).Any(e => e is Load or Intrinsic or Unknown)
                            && !(frame ??= new FrameModel(state.function, state.owner.CallEffects)).Escaped(slot.Offset, spill.Value.Width / 8):
                            line.Spills.Add((slot.Offset, spill.Value.Width, Normal(spill.Value, line, 0)));
                            break;
                        case Store store when !shared && !ExprMap.Walk(store.Value).Concat(ExprMap.Walk(store.Target)).Any(e => e is Intrinsic or Unknown):
                            line.Stores.Add((Normal(store.Target, line, 0), Normal(store.Value, line, 0)));
                            break;
                        case Call call when initializing && Initializes(call, line):
                            line.Initialized = true;
                            // 方法末尾的初始化是尾调用：它返回之后就是方法返回。
                            if (call.Kind != CallKind.Tail) break;
                            line.Previous = current;
                            line.Returned = new Return(call.Address);
                            return true;
                        case Call { Op.Kind: OpKind.NullCheck, Arguments: [var subject] } when !shared && (initializing || Checked(Normal(subject, line, 0), line)):
                            // 初始化一臂重新读取之后再判空，快路径复用已判过的值：保留这一臂时判空照常执行，结果相同。
                            break;
                        default:
                            reason = $"路径上有其他语句 0x{statement.Address:x}";
                            return false;
                    }
                }
                if (onward is not null) { (previous, current) = (current, onward); continue; }
                if (current.NormalSuccessors.ToArray() is not [var next]) { reason = "路径没有唯一后继"; return false; }
                (previous, current) = (current, next);
            }
        }

        /// <summary>分支的一侧只抛出（以不返回的调用结束）时，另一侧是继续执行的路径。</summary>
        private static Block? Survivor(Block block)
        {
            static bool Throws(Block target) => !target.NormalSuccessors.Any() && target.Statements.LastOrDefault() is Call { DoesNotReturn: true };
            return block.NormalSuccessors.ToArray() is [var first, var second] && first != second
                ? Throws(first) && !Throws(second) ? second : Throws(second) && !Throws(first) ? first : null
                : null;
        }

        private bool Initializes(Call call, Line line)
        {
            var helper = state.owner.HelperOf(call)?.Kind;
            switch (guard.Kind)
            {
                case Kind.Metadata:
                    if (helper != HelperKind.InitMetadata || call.Arguments.FirstOrDefault() is not { } slot || Normal(slot, line, 0) is not Const address
                        || state.facts.Usages.At(address.Value) is null) return false;
                    line.Slots.Add(address.Value);
                    return true;
                case Kind.ClassConstructor:
                    return call.Op is { Kind: OpKind.RunClassConstructor } op ? Equals(op.Operand, guard.Type) : helper == HelperKind.ClassInit && Class(call, line);
                default:
                    if (call.Op is { Kind: OpKind.InitClass } init ? !Equals(init.Operand, guard.Type) : !(helper == HelperKind.InitializedTypeInfo && Class(call, line))) return false;
                    // 初始化返回类本身。
                    if (call.Defines is [{ } result, ..]) line.Values[result] = new Symbol(SymbolKind.Class, guard.Type!);
                    return true;
            }
        }

        private bool Class(Call call, Line line) => call.Arguments.FirstOrDefault() is { } argument
            && Normal(argument, line, 0) is Symbol { Kind: SymbolKind.Class, Value: var type } && Equals(type, guard.Type);

        /// <summary>归一为守卫处的内容：路径上的定义代入，读取经守卫之前的写入转发；被改写过的旧读取保持为变量。</summary>
        private Expr Normal(Expr expression, Line line, int depth)
        {
            if (depth > Depth) return expression;
            return Fold.Simplify(ExprMap.Map(expression, part =>
            {
                switch (part)
                {
                    case Use use:
                        {
                            Expr value;
                            if (line.Values.TryGetValue(use.Variable, out var known)) value = known;
                            else if (line.Expanded.TryGetValue(use.Variable, out var expanded)) value = expanded;
                            else if (copies.TryGetValue(use.Variable, out var alias))
                                value = line.Expanded[use.Variable] = Normal(new Use(alias, use.Width), line, depth + 1);
                            else if (definitions.TryGetValue(use.Variable, out var source)
                                && (!ExprMap.Walk(source).Any(e => e is Load) || Unchanged(use.Variable, source)))
                                value = line.Expanded[use.Variable] = Normal(source, line, depth + 1);
                            else return null;
                            return value.Width == use.Width ? value : Fold.Convert(ConvKind.Truncate, value, use.Width);
                        }
                    case Load load:
                        if (line.Stores.Count > 0) loadAfterStore = true;
                        var read = new Load(Normal(load.Address, line, depth + 1), load.Width);
                        if (read.Address is FrameAddress { Offset: var at })
                            for (var i = line.Spills.Count - 1; i >= 0; i--)
                            {
                                var spill = line.Spills[i];
                                if (at + read.Width / 8 <= spill.Offset || spill.Offset + spill.Width / 8 <= at) continue;
                                if (at >= spill.Offset && at + read.Width / 8 <= spill.Offset + spill.Width / 8)
                                    return Fold.Extract(spill.Value, checked((int)(at - spill.Offset) * 8), read.Width);
                                loadAfterStore = true;
                                return read;
                            }
                        return Content(read, line, depth + 1);
                    default:
                        return null;
                }
            }));
        }

        /// <summary>守卫之前覆盖这段内存的写入给出它的值；遇到可能改写的语句就停止转发。</summary>
        private Expr Content(Load load, Line line, int depth)
        {
            var (root, offset) = SplitPass.Split(load.Address);
            var bytes = (ulong)load.Width / 8;
            foreach (var statement in Before())
            {
                if (statement is Store store)
                {
                    var (written, at) = SplitPass.Split(Normal(store.Target, line, depth + 1));
                    var size = (ulong)store.Value.Width / 8;
                    if (written == root && offset >= at && offset - at + bytes <= size)
                        return Fold.Extract(Normal(store.Value, line, depth + 1), checked((int)(offset - at) * 8), load.Width);
                    if (!Disjoint(written, at, size, root, offset, bytes)) return load;
                }
                else if (statement is Call call && !KeepsMemory(call)) return load;
            }
            return load;
        }

        /// <summary>变量读取的内存从读取处到守卫，每条路径上都没有被改写。</summary>
        private bool Unchanged(Variable variable, Expr source)
        {
            if (unchanged.TryGetValue(variable, out var known)) return known;
            return unchanged[variable] = Prove(variable, source);
        }

        private bool Prove(Variable variable, Expr source)
        {
            if (!owners.TryGetValue(variable, out var owner)) return false;
            var reads = ExprMap.Walk(source).OfType<Load>().Select(l => SplitPass.Split(state.defs.Expand(l.Address)) is var (root, offset) ? (Root: root, Offset: offset, Bytes: (ulong)l.Width / 8) : default).ToArray();
            bool Keeps(Stmt statement) => statement switch
            {
                Store store => SplitPass.Split(state.defs.Expand(store.Target)) is var (written, at)
                    && reads.All(r => Disjoint(written, at, (ulong)store.Value.Width / 8, r.Root, r.Offset, r.Bytes)),
                Call call => KeepsMemory(call),
                _ => true,
            };
            var visited = new HashSet<Block>();
            var pending = new Stack<Block>();
            pending.Push(head);
            while (pending.TryPop(out var block))
            {
                if (!visited.Add(block)) continue;
                if (block == owner)
                {
                    var from = block.Statements.FindIndex(s => s is Define define && define.Target == variable);
                    if (!block.Statements.Skip(from + 1).All(Keeps)) return false;
                    continue;
                }
                if (block.IsLandingPad || block == state.function.EntryBlock || !block.Statements.All(Keeps)) return false;
                foreach (var predecessor in block.Predecessors) pending.Push(predecessor);
            }
            return true;
        }

        private bool Checked(Expr subject, Line line)
        {
            foreach (var statement in Before())
                if (statement is Call { Op.Kind: OpKind.NullCheck, Arguments: [var value] } && Normal(value, line, Depth / 2) == subject) return true;
            return false;
        }

        private Block? ReloadedNonNull(Branch branch, Block block, Line line)
        {
            if (Normal(branch.Condition, line, 0) is not Compare { Cond: Cond.Eq or Cond.Ne, Left: Load subject, Right: Const { Value: 0 } } compare)
                return null;
            foreach (var statement in Before())
            {
                if (statement is Store || statement is Call call && !KeepsMemory(call)) return null;
                if (statement is Call { Op.Kind: OpKind.NullCheck, Arguments: [Use checkedValue] }
                    && definitions.TryGetValue(checkedValue.Variable, out var read) && Unchanged(checkedValue.Variable, read)
                    && Normal(checkedValue, line, 0) == subject)
                {
                    var destination = compare.Cond == Cond.Eq ? branch.Fallthrough : branch.Target;
                    return block.NormalSuccessors.SingleOrDefault(b => b.Start == destination);
                }
            }
            return null;
        }

        /// <summary>从守卫沿唯一前驱向前的语句。</summary>
        private IEnumerable<Stmt> Before()
        {
            var visited = new HashSet<Block>();
            var block = head;
            while (visited.Add(block))
            {
                for (var i = block.Statements.Count - 1; i >= 0; i--) yield return block.Statements[i];
                if (block.IsLandingPad || block.Predecessors is not [var previous]) yield break;
                block = previous;
            }
        }

        private bool KeepsMemory(Call call) => call.Op is { } op ? RuntimeOps.Of(op.Kind)?.Behavior.InvalidatesMemory == false
            : state.owner.HelperOf(call)?.Kind is HelperKind.InitMetadata or HelperKind.InitializedTypeInfo;

        private bool Disjoint(Expr written, ulong at, ulong size, Expr root, ulong offset, ulong bytes)
        {
            var separate = offset + bytes <= at || at + size <= offset;
            if (written == root) return separate;
            // 栈与元数据不是对象字段；托管引用只指向对象起点，不同偏移的字段不重叠。
            if (written is FrameAddress != root is FrameAddress) return true;
            if (written is Symbol || root is Symbol) return false;
            types ??= DelegateCallNormalizer.TypeQuery(state.facts, state.function);
            return separate && at >= RuntimeLayout.ObjectHeaderSize && offset >= RuntimeLayout.ObjectHeaderSize
                && types(written) is { } a && ManagedTypes.IsReferenceType(a) && types(root) is { } b && ManagedTypes.IsReferenceType(b);
        }

        /// <summary>表达式观察变量的位数：与常量掩码相与时只观察掩码内的位（bool 结果常写成 x &amp; 0xff）。</summary>
        private static int ObservedBits(Expr expression, Variable variable) => expression switch
        {
            Use use => use.Variable == variable ? use.Width : 0,
            Binary { Op: BinOp.And, Left: Use { } masked, Right: Const mask } when masked.Variable == variable => System.Numerics.BitOperations.Log2(mask.Value | 1) + 1,
            _ => ExprMap.Children(expression).Select(child => ObservedBits(child, variable)).DefaultIfEmpty(0).Max(),
        };

        /// <summary>同一个值的不同写法归一：走 fmov 还是直接读内存只差一层位重解释；取高位可以写成移位后截断，也可以写成取位段。</summary>
        private static Expr Plain(Expr value) => Fold.Simplify(ExprMap.Map(value, part => part switch
        {
            Ir.Convert { Kind: ConvKind.Bitcast } cast => Plain(cast.Operand),
            Ir.Convert { Kind: ConvKind.Truncate, Width: var width, Operand: Binary { Op: BinOp.LShr, Left: var whole, Right: Const shift } } => Fold.Extract(Plain(whole), (int)shift.Value, width),
            // 仅比较已观察到的值：小端读取的低位来自同一地址。
            Ir.Convert { Kind: ConvKind.Truncate, Operand: Load load } cast when cast.Width % 8 == 0 && cast.Width < load.Width
                => load with { Width = cast.Width },
            _ => null,
        }));

        private int Observed(Variable variable, HashSet<Variable> seen)
        {
            if (!seen.Add(variable)) return 0;
            var statements = state.function.Blocks.SelectMany(b => b.Statements).ToArray();
            var used = statements.Select(s => Observed(s, variable)).DefaultIfEmpty(0).Max();
            return Math.Max(used, statements.OfType<Phi>().Where(p => p.Sources.Contains(variable)).Select(p => Observed(p.Target, seen)).DefaultIfEmpty(0).Max());
        }

        private int Observed(Stmt statement, Variable variable)
        {
            // SIMD 寄存器保存整寄存器，标量实参只读取 ABI 指定的低位。
            if (variable.Storage.Bank != Bank.Vec || statement is not Call { Op: null, Kind: CallKind.Direct or CallKind.Tail } call
                || !call.Arguments.Any(a => a is Use use && use.Variable == variable)
                || state.owner.Shapes.Of(call).Signature is not { } signature)
                return ExprMap.Operands(statement).Select(e => ObservedBits(e, variable)).DefaultIfEmpty(0).Max();

            var used = Math.Max(ObservedBits(call.Target, variable), call.StackPointer is { } sp ? ObservedBits(sp, variable) : 0);
            if (call.DelegateReceiver is { } receiver) used = Math.Max(used, ObservedBits(receiver, variable));
            var sourceStart = call.Arguments.Length - call.Sources.Length;
            var pieces = signature.Arguments.SelectMany(a => a.Pieces).Where(p => p.Register.Bank == Bank.Vec).ToArray();
            for (var i = 0; i < call.Arguments.Length; i++)
            {
                var argument = call.Arguments[i];
                var width = ObservedBits(argument, variable);
                if (argument is Use && width > 0 && i >= sourceStart
                    && pieces.FirstOrDefault(p => p.Register == call.Sources[i - sourceStart]) is { } piece)
                    width = Math.Min(width, piece.Width);
                used = Math.Max(used, width);
            }
            return used;
        }
    }
}
