using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

/// <summary>合流后的首个地址读取分发到必经前驱，保留读取顺序和异常区域。</summary>
internal static class AddressPhiLoads
{
    public static bool Run(MachineFunction function, Func<int, Variable> fresh)
    {
        if (GuardedReads(function, fresh)) return true;
        var definitions = function.Blocks.SelectMany(b => b.Statements).OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
        Expr Expand(Expr expression, int depth = 0)
        {
            if (depth >= 16) return expression;
            if (expression is Use use)
                return definitions.TryGetValue(use.Variable, out var value) && Pure(value)
                    ? Fold.Convert(ConvKind.Truncate, Expand(value, depth + 1), use.Width) : expression;
            return ExprMap.Map(expression, part => part is Use ? Expand(part, depth + 1) : null);
        }
        static bool Pure(Expr value) => value switch
        {
            Const or Use or Symbol => true,
            Binary { Op: BinOp.Add or BinOp.Sub or BinOp.Mul or BinOp.Shl or BinOp.LShr or BinOp.AShr or BinOp.And or BinOp.Or or BinOp.Xor } binary
                => Pure(binary.Left) && Pure(binary.Right),
            Ir.Convert { Kind: ConvKind.ZeroExtend or ConvKind.SignExtend or ConvKind.Truncate } conversion => Pure(conversion.Operand),
            Compare comparison => Pure(comparison.Left) && Pure(comparison.Right),
            Select selection => Pure(selection.Condition) && Pure(selection.True) && Pure(selection.False),
            _ => false,
        };
        // 数组数据区起点上的读取，各边的地址是某个数组或它加比例下标（&a[0] 的下标已折叠），至少一边带下标。
        bool ElementChoice(Block block, Load load) => load.Address is Binary { Op: BinOp.Add, Left: Use { Width: 64 } chosen, Right: Const { Value: RuntimeLayout.ArrayData } }
            && block.Statements.OfType<Phi>().FirstOrDefault(p => p.Target == chosen.Variable) is { } selection && selection.Sources.Length == block.Predecessors.Count
            && selection.Sources.Select(s => Expand(new Use(s, 64))).ToArray() is var edges
            && edges.All(e => e is Use || Scaled(e)) && edges.Any(Scaled);
        bool Scaled(Expr edge) => edge is Binary { Op: BinOp.Add, Left: Use, Right: Binary { Op: BinOp.Shl, Right: Const { Value: <= 3 } } };
        // 这条入边上的地址是判空过的对象的字段：在这里读取不会抛出。
        bool CheckedField(Block parent, Expr address)
        {
            if (Expand(address) is not Binary { Op: BinOp.Add, Left: Use { Width: 64 } owner, Right: Const { Value: < 0x10000 } }) return false;
            for (var (block, depth) = (parent, 0); depth < 4; block = block.Predecessors[0], depth++)
            {
                if (block.Statements.OfType<Call>().Any(c => c is { Op.Kind: OpKind.NullCheck, Arguments: [Use used] } && used == owner)) return true;
                if (block.Predecessors.Count != 1) return false;
            }
            return false;
        }
        bool CheckedAddresses(Block block, Variable variable) => block.Statements.OfType<Phi>().FirstOrDefault(p => p.Target == variable) is { } merge
            && merge.Sources.Length == block.Predecessors.Count && block.Predecessors.Select((p, i) => CheckedField(p, new Use(merge.Sources[i], 64))).All(c => c);
        if (SunkReads(function, fresh, CheckedAddresses)) return true;
        var changed = false;
        foreach (var block in function.Blocks)
        {
            if (block.IsLandingPad
                || block.Predecessors.Count < 2 || block.Predecessors.Any(p => p == block || p.Handler != block.Handler && p.Handler is not null
                    || p.NormalSuccessors.Count() != 1 || !p.NormalSuccessors.Contains(block))) continue;
            // 合流块在异常区域内而前驱不在：只有不会抛出的字段读取可以移到前驱。
            if (block.Predecessors.Any(p => p.Handler != block.Handler) && !(block.Statements.FirstOrDefault(s => s is not (Phi or Nop)) is Define { Value: var entered }
                && ExprMap.Walk(entered).OfType<Load>().ToArray() is [{ Address: Use entry }] && CheckedAddresses(block, entry.Variable))) continue;
            var first = block.Statements.FirstOrDefault(s => s is not (Phi or Nop));
            if (first is Store store && function.Range.ExceptionRegions.Length == 0 && block.Handler is null
                && Pure(store.Target) && Pure(store.Value)
                && (Expand(store.Target) is Binary { Op: BinOp.Add, Left: Use offsetTarget, Right: Const } ? offsetTarget : Expand(store.Target) as Use) is { } target
                && block.Statements.OfType<Phi>().Any(p => p.Target == target.Variable))
            {
                var phis = block.Statements.OfType<Phi>().ToArray();
                if (phis.Any(p => p.Sources.Length != block.Predecessors.Count)) continue;
                var copies = block.Predecessors.Select((_, side) =>
                    new PathCopy(v => fresh(v.Width), phis.ToDictionary(p => p.Target, p => p.Sources[side]))
                        .Copy(store with { Target = Expand(store.Target) })).OfType<Store>().ToArray();
                if (copies.Length != block.Predecessors.Count || copies.Any(s => ExprMap.Operands(s).SelectMany(ExprMap.Walk)
                    .OfType<Use>().Any(u => u.Variable.Version < 0 || phis.Any(p => p.Target == u.Variable)))) continue;
                foreach (var (parent, copy) in block.Predecessors.Zip(copies))
                    parent.Statements.Insert(parent.Statements.Count - (parent.Statements.LastOrDefault() is Jump ? 1 : 0), copy);
                block.Statements.Remove(store);
                changed = true;
                continue;
            }
            var leading = block.Statements.FirstOrDefault(s => s is not (Phi or Nop) && !(s is Define d && Pure(d.Value)));
            // 返回值直接读取合流的元素地址：先把读取单独取出。
            if (leading is Return { Pieces: [{ Value: Load returned } piece] } ret && ElementChoice(block, returned))
            {
                var loaded = fresh(returned.Width);
                var position = block.Statements.IndexOf(ret);
                block.Statements[position] = ret with { Pieces = [piece with { Value = new Use(loaded, returned.Width) }] };
                block.Statements.Insert(position, leading = new Define(ret.Address, loaded, returned));
            }
            if (leading is not Define read) continue;
            var value = read.Value;
            while (value is Ir.Convert { Kind: ConvKind.ZeroExtend or ConvKind.SignExtend or ConvKind.Truncate } conversion)
                value = conversion.Operand;
            if (value is not Load load || load.Address.Width != 64 || !Pure(load.Address)) continue;
            var merged = block.Statements.OfType<Phi>().ToDictionary(p => p.Target);
            var address = Expand(load.Address);
            var inputs = ExprMap.Walk(address).OfType<Use>().Where(u => merged.ContainsKey(u.Variable)).ToArray();
            if (inputs.Length == 0 || inputs.Any(u => merged[u.Variable].Sources.Length != block.Predecessors.Count)) continue;
            // 基址和索引可分别合流；同一前驱的所有来源一起代入，不能交叉组合。
            var addresses = Enumerable.Range(0, block.Predecessors.Count).Select(i => Fold.Simplify(Expand(ExprMap.Map(address,
                e => e is Use use && merged.TryGetValue(use.Variable, out var phi) ? new Use(phi.Sources[i], use.Width) : null)))).ToArray();
            if (addresses.Any(e => !Pure(e) || ExprMap.Walk(e).OfType<Use>().Any(u => merged.ContainsKey(u.Variable)))) continue;
            // 分别合流的扩展限于变址常量表；普通字段、数组保留原读取形态，供后续访问器认回。
            var direct = load.Address is Use pointer && merged.ContainsKey(pointer.Variable);
            var typed = addresses.Select((address, side) =>
            {
                if (address is not Binary { Op: BinOp.Add, Left: Use owner, Right: Const offset }) return null;
                // 每条边独立通过了类型检查，偏移必须从该边的有类型引用解释。
                var pred = block.Predecessors[side];
                return pred.Predecessors is [var test] && test.NormalSuccessors.Contains(pred)
                    && test.Statements.LastOrDefault() is Branch
                    && pred.Statements.OfType<Call>().FirstOrDefault(c =>
                    c is { Op.Kind: OpKind.IsInst, Arguments: [Use original], Defines: [var result] }
                    && original == owner && result.Width == owner.Width && c.Address == test.Statements[^1].Address) is { } cast
                    ? (Expr)(new Binary(BinOp.Add, new Use(cast.Defines[0], owner.Width), offset, 64)) : null;
            }).ToArray();
            if (typed.All(e => e is not null)) addresses = typed.Select(e => e!).ToArray();
            else if (!direct && !ElementChoice(block, load) && addresses.Any(e => e is not Binary { Op: BinOp.Add, Right: Const, Left: Binary { Op: BinOp.Shl, Right: Const scale } }
                || scale.Value > 3 || (1UL << (int)scale.Value) != (ulong)load.Width / 8)) continue;
            var sources = new Variable[addresses.Length];
            for (var i = 0; i < sources.Length; i++)
            {
                var parent = block.Predecessors[i];
                sources[i] = fresh(read.Target.Width);
                var last = parent.Statements.LastOrDefault();
                parent.Statements.Insert(parent.Statements.Count - (last is Jump ? 1 : 0),
                    new Define(read.Address, sources[i], ExprMap.Map(read.Value, e => ReferenceEquals(e, load) ? load with { Address = addresses[i] } : null)));
            }
            block.Statements.Remove(read);
            block.Statements.Insert(block.Statements.TakeWhile(s => s is Phi).Count(), new Phi(read.Address, read.Target, sources));
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// 合流的地址各自是判空过的对象的字段，读取被下沉到合流之后的某个分支里，其间没有内存写入：提回合流处，与另一侧的读取并列。
    /// </summary>
    private static bool SunkReads(MachineFunction function, Func<int, Variable> fresh, Func<Block, Variable, bool> checkedAddresses)
    {
        // 各 φ 的使用：只有一条语句用到时是哪一条。
        var targets = function.Blocks.Where(b => !b.IsLandingPad && b.Predecessors.Count >= 2).SelectMany(b => b.Statements.OfType<Phi>())
            .Where(p => p.Target.Width == 64).Select(p => p.Target).ToHashSet();
        if (targets.Count == 0) return false;
        var users = new Dictionary<Variable, (Block Block, Stmt Statement, int Count)>();
        foreach (var user in function.Blocks)
            foreach (var statement in user.Statements)
                foreach (var variable in ExprMap.Operands(statement).SelectMany(ExprMap.Walk).OfType<Use>().Select(u => u.Variable)
                    .Concat(statement is Phi merged ? merged.Sources : []).Where(targets.Contains).Distinct())
                    users[variable] = (user, statement, users.GetValueOrDefault(variable).Count + 1);
        foreach (var join in function.Blocks)
        {
            if (join.IsLandingPad || join.Predecessors.Count < 2) continue;
            foreach (var phi in join.Statements.OfType<Phi>().Where(p => p.Target.Width == 64).ToArray())
            {
                if (!users.TryGetValue(phi.Target, out var only) || only.Count != 1) continue;
                var (block, statement, _) = only;
                if (block == join || statement is not Define define
                    || ExprMap.Walk(define.Value).OfType<Load>().ToArray() is not [{ Address: Use { Width: 64 } } load]
                    || ExprMap.Walk(define.Value).OfType<Use>().Count(u => u.Variable == phi.Target) != 1 || !checkedAddresses(join, phi.Target)
                    || !Undisturbed(join, block, statement)) continue;
                var value = fresh(load.Width);
                block.Statements[block.Statements.IndexOf(statement)] = define with { Value = ExprMap.Map(define.Value, e => ReferenceEquals(e, load) ? new Use(value, load.Width) : null) };
                join.Statements.Insert(join.Statements.TakeWhile(s => s is Phi).Count(), new Define(define.Address, value, load));
                return true;
            }
        }
        return false;

        // 从合流到读取的所有路径上没有内存写入，路径都经过合流。
        static bool Undisturbed(Block join, Block block, Stmt read)
        {
            static bool Writes(Stmt statement) => statement is Store or Opaque || statement is Call call && call.Op?.Kind is not (OpKind.NullCheck or OpKind.BoundsCheck);
            if (block.Statements.TakeWhile(s => s != read).Any(Writes)) return false;
            var pending = new Stack<Block>(block.Predecessors);
            var seen = new HashSet<Block> { block };
            while (pending.TryPop(out var current))
            {
                if (!seen.Add(current)) continue;
                if (current.Statements.Any(Writes) || seen.Count > 16) return false;
                if (current == join) continue;
                if (current.Predecessors.Count == 0) return false;
                foreach (var parent in current.Predecessors) pending.Push(parent);
            }
            return true;
        }
    }

    /// <summary>地址选择之后的判空、边界检查与读取一起移入原分支，不让读取越过检查。</summary>
    private static bool GuardedReads(MachineFunction function, Func<int, Variable> fresh)
    {
        if (function.Range.ExceptionRegions.Length != 0) return false;
        static Expr Unwrap(Expr value)
        {
            while (value is Ir.Convert { Kind: ConvKind.ZeroExtend or ConvKind.SignExtend or ConvKind.Truncate } conversion) value = conversion.Operand;
            return value;
        }
        foreach (var join in function.Blocks)
        {
            var phis = join.Statements.OfType<Phi>().ToDictionary(p => p.Target);
            if (join.IsLandingPad || join.Handler is not null || join.Predecessors.Count < 2 || phis.Count == 0
                || phis.Values.Any(p => p.Sources.Length != join.Predecessors.Count)
                || join.Predecessors.Any(p => p == join || p.Handler is not null || p.NormalSuccessors.ToArray() is not [var next] || next != join)
                || join.Statements.FirstOrDefault(s => s is not (Phi or Nop)) is not Call { Op.Kind: OpKind.NullCheck }) continue;
            var prefix = new List<(Block Block, Stmt Statement)>();
            var current = join;
            var visited = new HashSet<Block>();
            var reading = false;
            var done = false;
            while (!done && visited.Count < 8 && visited.Add(current))
            {
                foreach (var statement in current.Statements.Where(s => s is not (Phi or Nop or Jump)))
                {
                    if (statement is Define read && Unwrap(read.Value) is Load { Address: Use pointer }
                        && phis.ContainsKey(pointer.Variable)) reading = true;
                    else if (reading || statement is not Call { Op.Kind: OpKind.NullCheck or OpKind.BoundsCheck, Defines.Length: 0 }
                        && (statement is not Define length || Unwrap(length.Value) is not Load
                        { Width: 32, Address: Binary { Op: BinOp.Add, Right: Const { Value: Facts.RuntimeLayout.ArrayMaxLength } } }))
                    { done = true; break; }
                    prefix.Add((current, statement));
                    if (prefix.Count >= 16) { done = true; break; }
                }
                if (reading || done || current.NormalSuccessors.ToArray() is not [var next] || next.IsLandingPad || next.Handler is not null
                    || next.Predecessors.Count != 1 || next.Statements.Any(s => s is Phi)) break;
                current = next;
            }
            if (!reading) continue;
            var definitions = prefix.Select(p => p.Statement).OfType<Define>().ToArray();
            var results = definitions.ToDictionary(d => d.Target, _ => new List<Variable>());
            for (var side = 0; side < join.Predecessors.Count; side++)
            {
                var path = new PathCopy(v => fresh(v.Width), phis.ToDictionary(p => p.Key, p => p.Value.Sources[side]));
                var parent = join.Predecessors[side];
                var position = parent.Statements.Count - (parent.Statements.LastOrDefault() is Jump ? 1 : 0);
                foreach (var (_, statement) in prefix)
                {
                    parent.Statements.Insert(position++, path.Copy(statement));
                    if (statement is Define define) results[define.Target].Add(path.Map[define.Target]);
                }
            }
            foreach (var (block, statement) in prefix) block.Statements.Remove(statement);
            foreach (var definition in definitions) join.Statements.Insert(0, new Phi(definition.Address, definition.Target, results[definition.Target].ToArray()));
            return true;
        }
        return false;
    }
}
