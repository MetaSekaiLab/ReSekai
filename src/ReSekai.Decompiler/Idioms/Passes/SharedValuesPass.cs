using ReSekai.Decompiler.Facts;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Storage;

namespace ReSekai.Decompiler.Idioms;

/// <summary>
/// 完全共享泛型的不透明值。T 的大小和种类只有运行时元数据可知，codegen 只经地址操作它：
/// 句柄对值类型是数据指针，对引用类型是引用本身；形参地址写成 isValue(T) ? h : &amp;saved，invoker 槽写成 isValue(T) ? a : *a。
/// 这里把类元数据、动态分配和表示转换命名一次：动态分配成为类型为 T 的局部，两种转换都归一为同一个句柄，
/// 按 sizeof(T) 的复制和清零成为对 T 位置的操作。下游只按位置处理 T，不再观察这些写法。
/// </summary>
internal static class SharedValuesPass
{
    internal static bool SharedValues(this IdiomState state)
    {
        var changed = NameClassProperties(state);
        if (changed) state.defs = new(state.function);
        if (NameAllocations(state)) { changed = true; state.defs = new(state.function); }
        changed |= ValueSelections(state);
        changed |= ValueBranches(state);
        changed |= FieldWrites(state);
        changed |= CopyOperations(state);
        changed |= AlreadyClearedField(state);
        return changed;
    }

    private static bool NameClassProperties(IdiomState state)
    {
        var changed = false;
        Expr? Name(Expr part)
        {
            switch (part)
            {
                case Load { Width: 32 } load when state.defs.Expand(load.Address) is Binary { Op: BinOp.Add, Left: Symbol { Kind: SymbolKind.Class, Value: TypeRef type },
                        Right: Const { Value: RuntimeLayout.ClassStackSlotSize } }:
                    changed = true;
                    return new ClassProperty(type, ClassPropertyKind.StackSlotSize, 32);
                case Compare when IsValueTest(state.defs.Expand(part)) is { } test:
                    changed = true;
                    return test;
                default:
                    return null;
            }
        }
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
                if (ExprMap.Operands(block.Statements[i]).SelectMany(ExprMap.Walk).Any(e => e is Load or Compare))
                {
                    var before = changed;
                    changed = false;
                    var rewritten = ExprMap.MapOperands(block.Statements[i], e => ExprMap.Map(e, Name));
                    if (changed) block.Statements[i] = rewritten;
                    changed |= before;
                }
        return changed;
    }

    /// <summary>类的 byval_arg 标志最高位是值类型位；条件按该位取两值时方向相反即值类型测试。</summary>
    private static Expr? IsValueTest(Expr condition)
    {
        var loads = ExprMap.Walk(condition).OfType<Load>().Distinct().ToArray();
        if (loads is not [{ Width: 32, Address: Binary { Op: BinOp.Add, Left: Symbol { Kind: SymbolKind.Class, Value: TypeRef type },
                Right: Const { Value: RuntimeLayout.ClassByvalArg + 8 } } } flag]) return null;
        var value = DirectionPass.Direction(condition, e => e == flag ? Expr.C(0x80000000, 32) : null);
        var other = DirectionPass.Direction(condition, e => e == flag ? Expr.C(0, 32) : null);
        if (value is not (0 or 1) || other != 1 - value) return null;
        var test = new ClassProperty(type, ClassPropertyKind.IsValueType, 1);
        return value == 0 ? test : new Not(test);
    }

    private static TypeRef? SlotSizeOf(Expr expanded) => expanded switch
    {
        ClassProperty { Kind: ClassPropertyKind.StackSlotSize } size => size.Type,
        Ir.Convert { Kind: ConvKind.ZeroExtend or ConvKind.Truncate } conversion => SlotSizeOf(conversion.Operand),
        _ => null,
    };

    /// <summary>向下分配一个按 16 字节对齐的 stackslot_size(T)：T 的局部。</summary>
    private static bool NameAllocations(IdiomState state)
    {
        var changed = false;
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not Define { Value: Binary { Op: BinOp.Sub, Left: var from, Right: var aligned } } define
                    || state.defs.Expand(aligned) is not Binary { Op: BinOp.And, Left: Binary { Op: BinOp.Add, Left: var size, Right: Const { Value: 15 } },
                        Right: Const { Value: 0x1fffffff0 or 0xfffffffffffffff0 } }
                    || SlotSizeOf(size) is not { } type
                    || state.defs.Expand(from) is not (FrameAddress or Binary { Op: BinOp.Sub } or StackAllocation)) continue;
                block.Statements[i] = define with { Value = new StackAllocation(define.Address, type) };
                changed = true;
            }
        return changed;
    }

    private static (TypeRef Type, Expr Value, Expr Reference)? ValueSelection(Select select) => select.Condition switch
    {
        ClassProperty { Kind: ClassPropertyKind.IsValueType } test => (test.Type, select.True, select.False),
        Not { Operand: ClassProperty { Kind: ClassPropertyKind.IsValueType } test } => (test.Type, select.False, select.True),
        _ => null,
    };

    /// <summary>isValue(T) ? h : &amp;saved（saved 保存 h）与 isValue(T) ? a : *a 都是同一个句柄。</summary>
    private static bool ValueSelections(IdiomState state)
    {
        FrameModel? model = null;
        var changed = false;
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                var index = i;
                var rewritten = false;
                Expr? Handle(Expr part)
                {
                    if (part is not Select select || ValueSelection(select) is not var (_, value, reference)) return null;
                    var handle = state.defs.Expand(value);
                    if (state.defs.Expand(reference) == new Load(handle, 64)) { rewritten = true; return value; }
                    model ??= new FrameModel(state.function, state.owner.CallEffects);
                    // 保存槽的地址只作为形参地址传出，被调方不改写它。
                    if (model.Slot(reference) is not { } saved || model.Reach(block, index, saved, 8, throughCalls: true) is not { Complete: true, Stores: [var store] }
                        || state.defs.Expand(store.Value) != handle) return null;
                    rewritten = true;
                    return value;
                }
                var statement = ExprMap.MapOperands(block.Statements[i], e => ExprMap.Map(e, Handle));
                if (!rewritten && block.Statements[i] is Define { Value: Select { Condition: ClassProperty or Not } selection } && ValueSelection(selection) is not null)
                    state.Reject("shared.selection", block.Statements[i].Address, "值类型选择的两侧不是同一个句柄");
                if (!rewritten) continue;
                block.Statements[i] = statement;
                changed = true;
                model = null;
            }
        return changed;
    }

    /// <summary>同一类型的 IsValueType 在支配它的那条出边上已有定论：后一个分支只有一条出边可走。</summary>
    private static bool RepeatedValueTests(IdiomState state)
    {
        static (ClassProperty Property, bool ValueTaken)? Test(Block block) => block.Statements.LastOrDefault() is Branch branch && block.NormalSuccessors.Count() == 2
            ? branch.Condition switch
            {
                ClassProperty { Kind: ClassPropertyKind.IsValueType } property => (property, true),
                Not { Operand: ClassProperty { Kind: ClassPropertyKind.IsValueType } property } => (property, false),
                _ => null,
            } : null;
        var tests = state.function.Blocks.Where(b => Test(b) is not null).ToArray();
        if (tests.Length < 2) return false;
        foreach (var block in tests)
        {
            var (property, valueTaken) = Test(block)!.Value;
            foreach (var earlier in tests)
            {
                if (earlier == block || Test(earlier) is not var (known, knownTaken) || !known.Type.Equals(property.Type)) continue;
                var arms = earlier.NormalSuccessors.ToArray();
                for (var side = 0; side < 2; side++)
                {
                    if (arms[0] == arms[1] || arms[side].Predecessors is not [var only] || only != earlier || !Dominates(arms[side], block)) continue;
                    var isValue = side == 0 == knownTaken;
                    var kept = isValue == valueTaken ? 0 : 1;
                    var normal = block.NormalSuccessors.ToArray();
                    block.Statements[^1] = new Jump(block.Statements[^1].Address, normal[kept].Start);
                    MachineFunction.RemoveEdgeAt(block, 1 - kept);
                    return true;
                }
            }
        }
        return false;
        bool Dominates(Block through, Block target)
        {
            if (through == target) return true;
            var seen = new HashSet<Block> { through };
            var pending = new Stack<Block>([state.function.Blocks[0]]);
            while (pending.TryPop(out var current))
            {
                if (!seen.Add(current)) continue;
                if (current == target) return false;
                foreach (var next in current.Successors) pending.Push(next);
            }
            return true;
        }
    }

    /// <summary>按值类型分支、两臂只有纯定义、合流值都是同一句柄的两种表示时，分支没有托管意义。</summary>
    private static bool ValueBranches(IdiomState state)
    {
        var changed = RepeatedValueTests(state);
        if (changed) state.Cleanup();
        foreach (var head in state.function.Blocks)
        {
            if (head.Statements.LastOrDefault() is not Branch branch || head.NormalSuccessors.ToArray() is not [var taken, var fallthrough]) continue;
            var test = branch.Condition switch
            {
                ClassProperty { Kind: ClassPropertyKind.IsValueType } property => (Property: property, ValueTaken: true),
                Not { Operand: ClassProperty { Kind: ClassPropertyKind.IsValueType } property } => (Property: property, ValueTaken: false),
                _ => default,
            };
            if (test.Property is not { } classTest) continue;
            var (value, reference) = test.ValueTaken ? (taken, fallthrough) : (fallthrough, taken);
            static bool Pure(Stmt statement) => statement is Define or Nop or Jump;
            // 一臂是只有唯一前驱与唯一后继的块串；几个实参的两臂可能被合并成一组。
            static List<Block>? Arm(Block first, Block head, out Block? join)
            {
                join = null;
                var arm = new List<Block>();
                for (var (block, from) = (first, head); ;)
                {
                    if (block.Predecessors is not [var only] || only != from || block.IsLandingPad || arm.Contains(block) || block.Statements.Any(s => s is Phi)) { join = block; break; }
                    if (block.Handler is not null || block.NormalSuccessors.ToArray() is not [var next]) return null;
                    arm.Add(block);
                    (block, from) = (next, block);
                }
                return arm;
            }
            if (Arm(reference, head, out var join) is not { Count: > 0 } held || join is null || !held.SelectMany(b => b.Statements).All(Pure)
                || Arm(value, head, out var other) is not { } copied || other != join) continue;
            var valueEdge = join.Predecessors.IndexOf(copied.Count == 0 ? head : copied[^1]);
            var referenceEdge = join.Predecessors.IndexOf(held[^1]);
            if (valueEdge < 0 || referenceEdge < 0) continue;
            var phis = join.Statements.OfType<Phi>().ToArray();
            var copies = new Dictionary<Expr, Expr>();
            var independent = true;
            foreach (var statement in copied.SelectMany(b => b.Statements).Where(s => !Pure(s)))
            {
                independent = statement is Call { Op: { Kind: OpKind.CopyGenericValue, Operand: TypeRef copyType }, Arguments: [var target, var source] }
                    && copyType.Equals(classTest.Type)
                    && state.defs.Expand(target) is StackAllocation destination && state.defs.Expand(source) is StackAllocation origin && destination != origin
                    && destination.Element.Equals(copyType) && origin.Element.Equals(copyType)
                    && !copies.ContainsKey(origin) && !copies.ContainsValue(destination) && copies.TryAdd(destination, origin)
                    // 合流之后只有合流实参读取复制目标；之前的用法（复用的临时槽）不受影响。
                    && !After(join).SelectMany(b => b.Statements).Any(s => Escapes(s, destination));
                if (!independent) break;
            }
            if (!independent) continue;
            static HashSet<Block> After(Block start)
            {
                var seen = new HashSet<Block>();
                var pending = new Stack<Block>([start]);
                while (pending.TryPop(out var current))
                    if (seen.Add(current))
                        foreach (var next in current.Successors) pending.Push(next);
                return seen;
            }
            bool Escapes(Stmt statement, Expr destination)
            {
                bool Uses(Expr expression) => ExprMap.Walk(state.defs.Expand(expression)).Contains(destination);
                return statement switch
                {
                    Define definition when definition.Value == destination => false,
                    Phi phi => phi.Sources.Where((_, edge) => !phis.Contains(phi) || edge != valueEdge).Any(v => Uses(new Use(v, v.Width))),
                    _ => ExprMap.Operands(statement).Any(Uses),
                };
            }
            var same = phis.All(phi =>
            {
                var handle = state.defs.Expand(new Use(phi.Sources[valueEdge], phi.Target.Width));
                var loaded = state.defs.Expand(new Use(phi.Sources[referenceEdge], phi.Target.Width));
                if (copies.TryGetValue(handle, out var copySource))
                    return loaded == new Load(copySource, 64) && OnlyByValueArguments(state, phi.Target);
                return handle == loaded || phi.Target.Width == 64 && loaded == new Load(handle, 64);
            });
            if (!same)
            {
                state.Reject("shared.branch", branch.Address, "值类型分支的合流值不是同一句柄");
                continue;
            }
            head.Statements[^1] = new Jump(branch.Address, value.Start);
            MachineFunction.RemoveEdge(head, reference);
            changed = true;
        }
        return changed;
    }

    private static bool OnlyByValueArguments(IdiomState state, Variable value)
    {
        bool Reads(Expr expression) => ExprMap.Walk(expression).OfType<Use>().Any(u => u.Variable == value);
        foreach (var statement in state.function.Blocks.SelectMany(b => b.Statements))
        {
            if (statement is Phi phi && phi.Sources.Contains(value)) return false;
            if (!ExprMap.Operands(statement).Any(Reads)) continue;
            if (statement is not Call call || Reads(call.Target) || call.DelegateReceiver is { } receiver && Reads(receiver)
                || call.StackPointer is { } pointer && Reads(pointer) || state.owner.Shapes.Of(call).Signature is not { } signature) return false;
            for (var i = 0; i < call.Arguments.Length; i++)
            {
                if (!Reads(call.Arguments[i])) continue;
                if (call.Arguments[i] is not Use use || use.Variable != value || i >= call.Sources.Length
                    || !signature.Arguments.Any(a => a is { Transfer: Transfer.Copy, Access: MemoryAccess.Read }
                        && a.Pointer?.Register == call.Sources[i] && ManagedAbi.SharedValue(a.Type))) return false;
            }
        }
        return true;
    }

    /// <summary>按 stackslot_size(T) 的复制与清零是对 T 位置的操作。</summary>
    /// <summary>经 FieldInfo 写字段的模板展开成字段地址加写入，之后与内联的形式相同。</summary>
    private static bool FieldWrites(IdiomState state)
    {
        var changed = false;
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not Call { Op: null, Target: Const target, Kind: CallKind.Direct or CallKind.Tail } call) continue;
                var width = SharedFieldTemplates.WriteWidth(state.facts, target.Value);
                var floating = width == 0 && SharedFieldTemplates.WritesDouble(state.facts, target.Value);
                if (floating) width = 64;
                if (width == 0) continue;
                Expr? Argument(int register) => call.ArgumentIndex(Storage.Gpr(register)) is >= 0 and var at ? call.Arguments[at] : null;
                var valueRegister = floating ? Storage.Vec(0) : Storage.Gpr(2);
                var payload = call.ArgumentIndex(valueRegister) is >= 0 and var at ? call.Arguments[at] : null;
                if (Argument(0) is not { } receiver || Argument(1) is not { } field || payload is not { } value) continue;
                var address = state.Fresh();
                var writes = new List<Stmt>
                {
                    new Call(call.Address, CallKind.Direct, Expr.C(0, 64))
                    {
                        Op = new(OpKind.FieldAddress, null), Arguments = [receiver, field], Sources = [Storage.Gpr(0), Storage.Gpr(1)],
                        Results = [address.Storage], Defines = [address],
                    },
                };
                if (width == 1)
                {
                    var bit = Fold.Simplify(new Binary(BinOp.And, value, Expr.C(1, value.Width), value.Width));
                    writes.Add(new Store(call.Address, new Use(address, 64), Fold.Simplify(new Extract(bit, 0, 8))));
                }
                else if (width == 128 && Argument(3) is { } high)
                    writes.Add(new Store(call.Address, new Use(address, 64), new Insert(Fold.Convert(ConvKind.ZeroExtend, value, 128), high, 64)));
                else if (width is 32 or 64) writes.Add(new Store(call.Address, new Use(address, 64), Fold.Simplify(new Extract(value, 0, width))));
                else if (Argument(3) is { } size)
                    writes.Add(new Call(call.Address, CallKind.Direct, Expr.C(0, 64))
                    {
                        Op = new(OpKind.Import, "memcpy"), Arguments = [new Use(address, 64), value, size],
                        Sources = [Storage.Gpr(0), Storage.Gpr(1), Storage.Gpr(2)],
                    });
                else continue;
                // 模板返回 void，原调用保守定义的寄存器只是被破坏，后续调用仍按寄存器状态引用它们。
                writes.AddRange(call.Defines.OfType<Variable>().Select(v => new Define(call.Address, v, new Unknown("字段写入模板未定义返回寄存器", v.Width))));
                if (call.Kind == CallKind.Tail) writes.Add(new Return(call.Address));
                block.Statements.RemoveAt(i);
                block.Statements.InsertRange(i, writes);
                i += writes.Count - 1;
                changed = true;
            }
        return changed;
    }

    private static bool CopyOperations(IdiomState state)
    {
        var changed = false;
        foreach (var block in state.function.Blocks)
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not Call { Op: { Kind: OpKind.Import, Operand: string symbol }, Arguments: [var target, var source, var size] } call
                    || SlotSizeOf(state.defs.Expand(size)) is not { } type) continue;
                if (symbol is "memcpy" or "memmove")
                    block.Statements[i] = call with { Op = new(OpKind.CopyGenericValue, type), Arguments = [target, source], Sources = [] };
                else if (symbol == "memset" && state.defs.Expand(source) is Const { Value: 0 })
                    block.Statements[i] = call with { Op = new(OpKind.ClearGenericValue, type), Arguments = [target], Sources = [] };
                else continue;
                changed = true;
            }
        return changed;
    }

    /// <summary>清零后的新局部尚未经过可观察的操作，按字段写入同样的零不会改变值。</summary>
    private static bool AlreadyClearedField(IdiomState state)
    {
        var changed = false;
        foreach (var block in state.function.Blocks.Where(b => b.Statements.Any(s => s is Call { Op.Kind: OpKind.ClearGenericValue })))
        {
            StackAllocation? cleared = null;
            var addresses = new Dictionary<Variable, int>();
            for (var i = 0; i < block.Statements.Count; i++)
            {
                switch (block.Statements[i])
                {
                    case Call { Op: { Kind: OpKind.ClearGenericValue, Operand: TypeRef type }, Arguments: [var target] }
                        when state.defs.Expand(target) is StackAllocation allocation && allocation.Element.Equals(type):
                        cleared = allocation;
                        addresses.Clear();
                        break;
                    case Call { Op.Kind: OpKind.FieldAddress, Arguments: [var receiver, var field], Defines: [{ } result] }
                        when cleared is not null && state.defs.Expand(receiver) == cleared
                        && state.defs.Expand(field) is Symbol { Kind: SymbolKind.Field, Value: FieldRef { DeclaringType: DefinitionRef owner } reference }
                        && !reference.Field.IsStatic && owner.Equals(cleared.Element):
                        var fieldType = reference.Field.Type.Substitute(owner.Arguments, []);
                        if (state.facts.Layouts.TrySizeAndAlignment(fieldType) is { Size: > 0 } fieldLayout)
                            addresses[result] = fieldLayout.Size * 8;
                        break;
                    case Store { Target: Use address, Value: var value } when addresses.TryGetValue(address.Variable, out var width)
                        && value.Width == width && state.defs.Expand(value) is Const { Value: 0 }:
                        block.Statements.RemoveAt(i--);
                        changed = true;
                        break;
                    case Define or Nop:
                        break;
                    default:
                        cleared = null;
                        addresses.Clear();
                        break;
                }
            }
        }
        return changed;
    }
}
